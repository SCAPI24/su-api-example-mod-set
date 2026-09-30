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

        /// <summary>
        /// **出厂异常子树**（plan §4.12）：读 `pkg.fail` → 问一次 Laya → 把答案翻译成稳定黑板旗标。
        /// 主树用 `Task.Subtree package=recover#root` 挂上即可（见 <see cref="RecoverDemoFile"/>）。
        /// </summary>
        public const string RecoverFile = "recover.scbtpak";

        /// <summary>**异常子树的接线示例**：一个"什么都不做、只等失败"的主树，失败时挂上异常子树。</summary>
        /// <summary>示例·异常子树接线（`demo.recover.scbtpak`）。</summary>
        public const string RecoverDemoFile = "demo.recover.scbtpak";

        /// <summary>示例·Laya 驱动的主循环（`demo.laya.scbtpak`）：Laya 出目标 → 出厂动作脚本执行。</summary>
        public const string LayaDemoFile = "demo.laya.scbtpak";

        /// <summary>
        /// **可复用的捡取包**（`pickup.scbtpak`）：找最近的掉落物 → 走过去（走过去就自动拾取）。
        ///
        /// 两种用法都成立，所以它既是"包"也是"树"：
        ///   · 被别的树调用：`Task.Subtree(package=pickup#root)`（打猎打完捡掉落就用这条）；
        ///   · 单独跑：`ai switch pickup` 就是一个"见什么捡什么"的巡航模式。
        /// 契约（黑板的 `pickup.*` 键）写在包自己的清单里。
        /// </summary>
        public const string PickupFile = "pickup.scbtpak";

        /// <summary>`pickup` 包的 id（`Task.Subtree package=<id>#root` 用它）。</summary>
        public const string PickupPackageId = "pickup";

        /// <summary>捡取包的搜索半径（米）：打完猎掉落就在脚边，6 格够用且不会满地图乱跑。</summary>
        public const float PickupRadius = 6f;

        /// <summary>`demo.laya` 的连败计数器键（树里唯一的"记忆"，见 `BtCounterTask`）。</summary>
        public const string FailCountKey = "fail.count";

        /// <summary>连败到这个数 → 不再问模型，改跑兜底脚本（C# 确定性）。</summary>
        public const int FallbackThreshold = 3;

        /// <summary>再连败到这个数 → 停手几秒（打断 token 花销，给世界/人一点时间）。</summary>
        public const int StandbyThreshold = 6;

        /// <summary>示例·世界外界面链（`demo.front.scbtpak`）：Laya 出界面动作 → 语义点击。</summary>
        public const string FrontDemoFile = "demo.front.scbtpak";

        /// <summary>异常子树写出的旗标键（主树用 `Blackboard` 装饰器读它们分流）。</summary>
        public const string FlagRetry = "pkg.retryNow";

        public const string FlagBackup = "pkg.useBackup";

        public const string FlagSkip = "pkg.skipStep";

        public const string FlagAbort = "pkg.abortPlan";

        /// <summary>出厂模板列表（顺序即安装顺序）。</summary>
        public static List<PackageTemplate> All()
        {
            return new List<PackageTemplate>
            {
                BuildCommon(),
                BuildDemo(),
                BuildWatch(),
                BuildFollow(),
                PlayerAiPackages.BuildTestTreeTemplate(),
                BuildRecover(),
                BuildRecoverDemo(),
                BuildPickup(),
                BuildLayaDemo(),
                BuildFrontDemo()
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

        /// <summary>出厂异常子树（§4.12）。</summary>
        public static PackageTemplate Recover()
        {
            return BuildRecover();
        }

        /// <summary>异常子树的接线示例。</summary>
        public static PackageTemplate RecoverDemo()
        {
            return BuildRecoverDemo();
        }

        /// <summary>Laya 驱动的主循环示例。</summary>
        public static PackageTemplate LayaDemo()
        {
            return BuildLayaDemo();
        }

        /// <summary>通用·捡掉落物（可被别的树调用，也能单独跑）。</summary>
        public static PackageTemplate Pickup()
        {
            return BuildPickup();
        }

        /// <summary>世界外界面链示例。</summary>
        public static PackageTemplate FrontDemo()
        {
            return BuildFrontDemo();
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

        // ---------------------------------------------------------------- 异常子树（§4.12）

        /// <summary>
        /// **出厂异常子树**（`recover.scbtpak`）——把 §4.12 的"自动问题判断"真正接进一棵树。
        ///
        /// <code>
        /// Root(loop=true)
        /// └─ Selector.sel
        ///    ├─ Sequence.recover   [装饰器 Blackboard(pkg.fail IsSet)]      ← 门：只在真出过错时进
        ///    │  ├─ Task.Log "pkg.fail set, asking Laya"
        ///    │  ├─ Task.LayaAsk questions=pkg_recover answerKeys=recover:recover:str
        ///    │  │              onUnavailable=default defaultValue=abort_plan   ← Laya 不可用时的确定性兜底
        ///    │  └─ Selector.by_answer
        ///    │     ├─ Sequence.do_retry  [recover == "retry"]      → pkg.retryNow=true ; 清 pkg.fail
        ///    │     ├─ Sequence.do_backup [recover == "use_backup"] → pkg.useBackup=true; 清 pkg.fail
        ///    │     ├─ Sequence.do_skip   [recover == "skip_step"]  → pkg.skipStep=true ; 清 pkg.fail
        ///    │     └─ Sequence.do_abort  [recover == "abort_plan"] → pkg.abortPlan=true; 清 pkg.fail
        ///    └─ Task.Wait 0.5                                        ← 没失败就待机
        /// </code>
        ///
        /// 三条设计：
        ///   1. **只问枚举，具体动作由 C# / 主树做**（§4.9）：这棵树把答案翻译成四个**稳定旗标**，
        ///      真正"重新装包 / 换备用库 / 跳过 / 停手"由各自的节点按旗标决定 —— 于是同一个异常子树
        ///      对任何主树都通用，而"重试几次""退避多久"这类确定性参数不归模型管；
        ///   2. **每个分支都清掉 `pkg.fail`**：不清的话门一直是开的，下一 tick 又会问一次（烧 token、刷日志）；
        ///   3. **`onUnavailable=default` + `defaultValue=abort_plan`**：Laya 不可用时**不问**，
        ///      直接落到最保守的那一支（§4.12 的熔断纪律：服务都连不上时不要再指望它出主意）。
        ///
        /// ⚠️ **`Root.loop=false` 是必须的**（A28）：`loop=true` 的 Root **永远返回 InProgress**
        /// （跑完立刻从头再来），于是把它挂在 `Task.Subtree` 下面的那棵主树会**永远等它**——
        /// 旗标摆好了却没人消费，看着就是"异常处理没反应"（实机踩过）。
        /// 设成 false 之后：**作为子树用**时跑完就把控制权还给调用它的树；
        /// **单独当主树用**时（一次性处置）跑完就停 —— 两种用法都正确。
        /// </summary>
        private static PackageTemplate BuildRecover()
        {
            PackageValue blackboard = Arr(
                Obj("name", Str(AssetFailureCodes.FailKey), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("失败标记（由 §4.12 的那条链写进来；本子树读完就清）")),
                Obj("name", Str("recover"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("Laya 的处置建议：retry / use_backup / skip_step / abort_plan")),
                Obj("name", Str(FlagRetry), "type", Str("bool"), "readonly", Bool(false),
                    "description", Str("建议：等一会儿重试同一个资源")),
                Obj("name", Str(FlagBackup), "type", Str("bool"), "readonly", Bool(false),
                    "description", Str("建议：切到备用问题库 / 动作集")),
                Obj("name", Str(FlagSkip), "type", Str("bool"), "readonly", Bool(false),
                    "description", Str("建议：跳过这一步，继续原计划")),
                Obj("name", Str(FlagAbort), "type", Str("bool"), "readonly", Bool(false),
                    "description", Str("建议：停手待命")));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("recover"),
                "name", Str("出厂·异常处理子树"),
                "entry", Str("root"),
                "blackboard", blackboard);

            // 每个分支：先立旗标，再清失败标记（顺序不能反：清了标记 Selector 会立刻重选，
            // 但同一 tick 内不会重新进入——旗标先写下来才不会被丢掉）。
            PackageValue branchRetry = RecoverBranch("do_retry", "retry", FlagRetry,
                "recover=retry -> retry the same resource");
            PackageValue branchBackup = RecoverBranch("do_backup", "use_backup", FlagBackup,
                "recover=use_backup -> switch to the backup question bank / action set");
            PackageValue branchSkip = RecoverBranch("do_skip", "skip_step", FlagSkip,
                "recover=skip_step -> skip this step and continue the plan");
            PackageValue branchAbort = RecoverBranch("do_abort", "abort_plan", FlagAbort,
                "recover=abort_plan -> stop the plan and stand by");

            PackageValue recover = Node("recover", "Sequence",
                null,
                Arr(Obj("id", Str("d_fail"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    // 用 `!= ""` 而不是 `IsSet`：`IsSet` 只看"键在不在"，而"写了空串"的键仍然在；
                    // 三种情形都要判对 —— 缺键=没失败、空串=没失败、有内容=真失败。
                    "properties", Obj(
                        "key", Str(AssetFailureCodes.FailKey),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("!="),
                        "value", Str(string.Empty)))),
                null,
                Arr(
                    Node("log_fail", "Task.Log", Obj(
                        "message", Str("pkg.fail is set -> asking the pkg_recover bank"))),
                    Node("ask", "Task.LayaAsk", Obj(
                        "questions", Str(QuestionBank.RecoverBankFileName),
                        "answerKeys", Str("recover:recover:str"),
                        "onUnavailable", Str("default"),
                        "defaultValue", Str("abort_plan"),
                        "writeFailKey", Bool(true),
                        "failKey", Str("recover.fail"),
                        "reasonKey", Str("recover.reason"))),
                    Node("by_answer", "Selector", null, null, null,
                        Arr(branchRetry, branchBackup, branchSkip, branchAbort))));

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(false)), null, null, Arr(
                Node("sel", "Selector", null, null, null, Arr(
                    recover,
                    Node("idle", "Task.Wait", Obj("seconds", Num(0.5)))))));

            return new PackageTemplate(RecoverFile, "出厂·异常处理子树",
                "读 pkg.fail → 问 Laya 一次 → 把答案翻译成 pkg.retryNow / pkg.useBackup /"
                + " pkg.skipStep / pkg.abortPlan 四个旗标。主树用 Task.Subtree 挂它。",
                manifest, tree);
        }

        /// <summary>异常子树里的一个分支：`recover == <answer>` → 立旗标 → 清 `pkg.fail`。</summary>
        private static PackageValue RecoverBranch(string id, string answer, string flag, string log)
        {
            return Node(id, "Sequence",
                null,
                Arr(Obj("id", Str("d_" + id), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str("recover"),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("=="),
                        "value", Str(answer)))),
                null,
                Arr(
                    Node(id + "_log", "Task.Log", Obj("message", Str(log))),
                    Node(id + "_flag", "Task.SetBlackboard", Obj(
                        "key", Str(flag), "valueKind", Str("bool"), "value", Bool(true))),
                    Node(id + "_clear", "Task.SetBlackboard", Obj(
                        "key", Str(AssetFailureCodes.FailKey),
                        "valueKind", Str("string"), "value", Str(string.Empty)))));
        }

        /// <summary>
        /// **异常子树的接线示例**（`demo.recover.scbtpak`）：把"门 + 子包 + **四个旗标消费者**"这一段完整摆出来。
        ///
        /// <code>
        /// Root(loop=true)
        /// └─ Selector.sel
        ///    ├─ Sequence.on_fail   [装饰器 Blackboard(pkg.fail IsSet)]
        ///    │  ├─ Task.Subtree package=recover#root        ← 交给出厂异常子树去问 Laya
        ///    │  └─ Task.Wait 0.2
        ///    ├─ Sequence.on_retry  [Blackboard(pkg.retryNow == true)]  → 记一行 + 清旗标
        ///    ├─ Sequence.on_backup [Blackboard(pkg.useBackup == true)] → 记一行 + 清旗标
        ///    ├─ Sequence.on_skip   [Blackboard(pkg.skipStep == true)]  → 记一行 + 清旗标
        ///    ├─ Sequence.on_abort  [Blackboard(pkg.abortPlan == true)] → 记一行 + 清旗标 + 等 1 s
        ///    └─ Task.Wait 0.5                                          ← 平时待机
        /// </code>
        ///
        /// **怎么接到自己的树上**：把最后那个 `Task.Wait 0.5` 换成你的工作分支，再按旗标决定它跑不跑 ——
        /// 例如给工作分支加装饰器 `Blackboard(pkg.abortPlan == false)`，或者让 `on_retry` 分支
        /// 顺手把"用哪个包"写进黑板（`Task.SetBlackboard key=work.script value=…`），
        /// 工作分支用 `Task.RunActionScript scriptKey=work.script` 读它。
        ///
        /// 每一支都**先记日志再清旗标**：不清的话下一 tick 还会命中同一个门（日志刷屏、看着像卡住）。
        /// </summary>
        private static PackageTemplate BuildRecoverDemo()
        {
            PackageValue references = Arr(
                Obj("id", Str("recover"), "path", Str(RecoverFile)));

            PackageValue blackboard = Arr(
                Obj("name", Str(AssetFailureCodes.FailKey), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("由 §4.12 那条链（或节点自己的 failKey）写入")),
                Obj("name", Str(PackageTemplates.FlagRetry), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str(PackageTemplates.FlagBackup), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str(PackageTemplates.FlagSkip), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str(PackageTemplates.FlagAbort), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str("work.script"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("示例：工作分支要跑的脚本名（由旗标分支决定）")));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("demo.recover"),
                "name", Str("示例·异常子树接线"),
                "entry", Str("root"),
                "blackboard", blackboard,
                "references", references);

            PackageValue onFail = Node("on_fail", "Sequence",
                null,
                Arr(Obj("id", Str("d_fail"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    // 用 `!= ""` 而不是 `IsSet`：`IsSet` 只看"键在不在"，而"写了空串"的键仍然在；
                    // 三种情形都要判对 —— 缺键=没失败、空串=没失败、有内容=真失败。
                    "properties", Obj(
                        "key", Str(AssetFailureCodes.FailKey),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("!="),
                        "value", Str(string.Empty)))),
                null,
                Arr(
                    Node("sub_recover", "Task.Subtree", Obj("package", Str("recover#root"))),
                    Node("fail_settle", "Task.Wait", Obj("seconds", Num(0.2)))));

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(true)), null, null, Arr(
                Node("sel", "Selector", null, null, null, Arr(
                    onFail,
                    FlagConsumer("on_retry", PackageTemplates.FlagRetry, "work.script",
                        "ui_click_play.aeact", "recover=retry -> would retry the same resource now", 0),
                    FlagConsumer("on_backup", PackageTemplates.FlagBackup, "work.script",
                        "turn_around.aeact", "recover=use_backup -> would switch to the backup set now", 0),
                    FlagConsumer("on_skip", PackageTemplates.FlagSkip, null, null,
                        "recover=skip_step -> would skip this step now", 0),
                    FlagConsumer("on_abort", PackageTemplates.FlagAbort, null, null,
                        "recover=abort_plan -> would stand by now", 1),
                    Node("idle", "Task.Wait", Obj("seconds", Num(0.5)))))));

            return new PackageTemplate(RecoverDemoFile, "示例·异常子树接线",
                "失败时（pkg.fail 被写进黑板）挂上出厂异常子树 recover.scbtpak#root，"
                + "再把它的四个旗标（pkg.retryNow / pkg.useBackup / pkg.skipStep / pkg.abortPlan）"
                + "各接一段处置；把最后的 Task.Wait 换成自己的工作分支就是实战用法。",
                manifest, tree);
        }

        /// <summary>
        /// 一个"旗标消费者"分支：`<flag> == true` → 记一行日志 → （可选）写 `work.script` → 清旗标 → （可选）等一会儿。
        /// </summary>
        private static PackageValue FlagConsumer(string id, string flag, string scriptKey,
            string scriptValue, string log, float waitSeconds)
        {
            var children = new List<PackageValue>
            {
                Node(id + "_log", "Task.Log", Obj("message", Str(log)))
            };
            if (!string.IsNullOrEmpty(scriptKey) && scriptValue != null)
            {
                children.Add(Node(id + "_script", "Task.SetBlackboard", Obj(
                    "key", Str(scriptKey), "valueKind", Str("string"), "value", Str(scriptValue))));
            }
            children.Add(Node(id + "_clear", "Task.SetBlackboard", Obj(
                "key", Str(flag), "valueKind", Str("bool"), "value", Bool(false))));
            if (waitSeconds > 0f)
                children.Add(Node(id + "_wait", "Task.Wait", Obj("seconds", Num(waitSeconds))));

            return Node(id, "Sequence",
                null,
                Arr(Obj("id", Str("d_" + id), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str(flag),
                        "query", Str("Compare"),
                        "valueKind", Str("bool"),
                        "operator", Str("=="),
                        "value", Bool(true)))),
                null,
                Arr(children.ToArray()));
        }

        // ---------------------------------------------------------------- Laya 主循环示例

        /// <summary>`goal`（Laya 给的枚举）→ 动作脚本名（C# 确定性给的）。**顺序即展示顺序**。</summary>
        private static readonly string[][] LayaGoalScripts =
        {
            new[] { "mine",    ActionScriptNames.MineOnce },
            new[] { "gather",  ActionScriptNames.GatherOnce },
            new[] { "craft",   ActionScriptNames.CraftOnce },
            new[] { "eat",     ActionScriptNames.EatOnce },
            new[] { "sleep",   ActionScriptNames.SleepOnce },
            new[] { "fight",   ActionScriptNames.AttackOnce },
            new[] { "flee",    ActionScriptNames.FleeOnce },
            new[] { "explore", ActionScriptNames.ExploreOnce }
        };

        /// <summary>
        /// **示例·Laya 驱动的主循环**（`demo.laya.scbtpak`）—— plan 里"Laya 进行为树"的主形态（§4.6 / C8）。
        ///
        /// <code>
        /// Root(loop=true)
        /// └─ Selector.sel
        ///    ├─ Sequence.on_action_fail  [Blackboard(action.fail != "")]   ← 动作失败先记账再清标记
        ///    ├─ Sequence.decide          [Cooldown 2.5s]                   ← 决策节奏（不每帧问）
        ///    │  ├─ Task.LayaAsk  questions=world_goal  only=goal,threat,can_reach
        ///    │  │                answerKeys=goal:goal:str,threat:threat:bool,can_reach:can_reach:bool
        ///    │  │                onUnavailable=keep                        ← 服务抖动时保留上次目标
        ///    │  ├─ Selector.dispatch                                       ← 枚举 → 脚本名（C# 查表）
        ///    │  │  ├─ Sequence.go_mine    [Blackboard(goal == "mine")]  → work.script = mine_stone_once.aeact
        ///    │  │  ├─ … 八个目标各一支 …
        ///    │  │  └─ Task.Wait 0.3                                        ← 没有目标（还没判出来）就待机
        ///    │  └─ Sequence.do          [Blackboard(work.script != "")]
        ///    │     ├─ Task.RunActionScript scriptKey=work.script
        ///    │     └─ Task.SetBlackboard work.script = ""                  ← 跑完就清，避免重跑旧动作
        ///    └─ Task.Wait 0.3
        /// </code>
        ///
        /// **三条不变量**（每条都对应一个"不做会出事"）：
        ///   · **模型只给枚举**：`goal` 是 `world_goal.qbank` 的选项 key，脚本名由 <see cref="LayaGoalScripts"/>
        ///     这张表映射 —— 模型从不说时长/角度/坐标（§4.9）；
        ///   · **决策有节奏**：`Cooldown 2.5 s` 让"问一次 → 跑完一个动作 → 再问"成为循环，
        ///     而不是每帧发请求（实测单次 150~600 ms，每帧问等于烧 token 且动作永远做不完）；
        ///   · **失败不静默**：动作失败由 `Task.RunActionScript` 写 `action.fail`，
        ///     下一 tick 由 `on_action_fail` 分支记一行日志并清掉 —— 不清理就会每 tick 命中同一个门（看着像卡死）。
        ///
        /// **怎么调**：`goal=craft` 现在落到占位脚本 `craft_once.aeact`（开/关背包），
        /// 换掉那条脚本就是真正的合成；想加目标就在 `world_goal.qbank` 里加选项，再来这里加一支。
        /// </summary>
        // ---------------------------------------------------------------- 可复用的捡取包（2026-09-26）

        /// <summary>
        /// **捡取包**（`pickup.scbtpak`）：最近的掉落物 → 走过去。
        ///
        /// 为什么单独成包（用户明确要求）："捡东西"不该只长在打猎分支里 ——
        /// 打完猎要捡掉落、以后"帮我捡一下"要用、别的主树也可能要，抽成包才能一处写、多处调。
        /// 它同时可以 `ai switch pickup` 单独跑（根是 `Root(loop=true)`，于是变成"见什么捡什么"）。
        ///
        /// 为什么"走过去"就等于"捡起来"：SC 的掉落物是**靠近自动拾取**的，
        /// 所以这一支的 KPI 是"走到 1 格内"，不需要额外的交互动作。
        ///
        /// 服务挂在**根 Selector** 上（不是挂在守卫分支里面）：服务只在活动路径上 tick，
        /// 挂进"需要它写的键"的分支里就是死锁（这条坑在聊天分支上实测踩过）。
        /// </summary>
        private static PackageTemplate BuildPickup()
        {
            PackageValue blackboard = Arr(
                Obj("name", Str("pickup.target"), "type", Str("actor"), "readonly", Bool(true),
                    "description", Str("最近的掉落物（服务 UpdateNearestPickable 写入；捡到就清）")),
                Obj("name", Str("pickup.targetIsSet"), "type", Str("bool"), "readonly", Bool(true),
                    "description", Str("附近有没有可捡的东西")),
                Obj("name", Str("pickup.targetDistance"), "type", Str("float"), "readonly", Bool(true)),
                Obj("name", Str("pickup.targetName"), "type", Str("string"), "readonly", Bool(true)));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str(PickupPackageId),
                "name", Str("通用·捡掉落物"),
                "entry", Str("root"),
                "blackboard", blackboard,
                "references", Arr());

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(true)), null, null,
                Arr(
                    Node("pickup_root", "Selector", null, null,
                        Arr(ServiceEntry("svc_pickable", "UpdateNearestPickable", Obj("properties", Obj(
                            "targetKey", Str("pickup.target"),
                            "maxDistance", Num(PickupRadius),
                            "clearWhenMissing", Bool(true))))),
                        Arr(
                        Node("pickup_round", "Sequence", null,
                            Arr(CompareDecorator("d_pickup_has_target", "pickup.targetIsSet",
                                "bool", "==", Bool(true), "None")),
                            null,
                            Arr(Node("pickup_go", "Task.FollowEntity", Obj(
                                "targetKey", Str("pickup.target"),
                                "keepDistance", Num(1f),
                                "arriveThenSucceed", Bool(true),
                                "timeout", Num(8f))))),
                        // 附近没东西可捡 → 待机（不空转、也不让根每帧判失败）
                        Node("pickup_idle", "Task.Wait", Obj("seconds", Num(0.3)))))));

            return new PackageTemplate(PickupFile, "通用·捡掉落物",
                "找最近的掉落物（半径 " + PickupRadius.ToString("0.#") + " 格）→ 走过去；"
                + "SC 的掉落物靠近即自动拾取，所以\"走到 1 格内\"就是\"捡起来了\"。"
                + "既可被别的树用 Task.Subtree(package=pickup#root) 调用（打猎打完捡掉落），"
                + "也可 ai switch pickup 单独当\"见什么捡什么\"用。",
                manifest, tree);
        }

        private static PackageTemplate BuildLayaDemo()
        {            PackageValue blackboard = Arr(
                Obj("name", Str("goal"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("Laya 判定的下一步目标（world_goal 的选项 key）")),
                Obj("name", Str("work.script"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("本轮的脚本名（由 goal 分支写，树不写死）")),
                Obj("name", Str("action.fail"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("动作失败的错误码（Task.RunActionScript 写）")),
                Obj("name", Str("action.reason"), "type", Str("string"), "readonly", Bool(false)),
                Obj("name", Str(FailCountKey), "type", Str("int"), "readonly", Bool(false),
                    "description", Str("连续失败次数（树里唯一的记忆）：到 "
                        + FallbackThreshold + " 换兜底脚本，到 " + StandbyThreshold + " 停手 5 秒")),
                Obj("name", Str("laya.fail"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("Laya 拿不到答案时的错误码（onUnavailable=keep 时目标保留）")),
                Obj("name", Str("laya.reason"), "type", Str("string"), "readonly", Bool(false)),
                // ---- 聊天监听（2026-09-26）：`Service.ChatWatch` 写，聊天分支读 ----
                Obj("name", Str("chat.pending"), "type", Str("bool"), "readonly", Bool(false),
                    "description", Str("有一条待处理指令（树处理完把它清掉）")),
                Obj("name", Str("chat.intent"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("意图：come / follow / stop / ignore（短语表命中或模型兜底判出）")),
                Obj("name", Str("chat.bank"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("短语表没命中时要问的问题库名；空 = 已命中，不必问模型")),
                Obj("name", Str("chat.digest"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("兜底判定的摘要：chat=<原话>（喂 Task.LayaAsk digestKey）")),
                Obj("name", Str("chat.text"), "type", Str("string"), "readonly", Bool(false)),
                Obj("name", Str("chat.sender"), "type", Str("string"), "readonly", Bool(false)),
                Obj("name", Str("chat.seq"), "type", Str("int"), "readonly", Bool(false)),
                Obj("name", Str("chat.target"), "type", Str("actor"), "readonly", Bool(false),
                    "description", Str("发话者（按名字找不到时退回「最近的另一个玩家」）")),
                Obj("name", Str("chat.targetIsSet"), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str("chat.targetDistance"), "type", Str("float"), "readonly", Bool(false)),
                Obj("name", Str("chat.targetName"), "type", Str("string"), "readonly", Bool(false)),
                Obj("name", Str("chat.arg"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("带参意图的参数（打猎的物种词，如 牛 / 牛,狼）")),
                // ---- 打猎（2026-09-26）：服务 UpdateNearestCreature(nameKey) 写，hunt 分支读 ----
                Obj("name", Str("hunt.target"), "type", Str("actor"), "readonly", Bool(false),
                    "description", Str("要打的生物（按名找；死了就清键）")),
                Obj("name", Str("hunt.targetIsSet"), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str("hunt.targetDistance"), "type", Str("float"), "readonly", Bool(false)),
                Obj("name", Str("hunt.targetName"), "type", Str("string"), "readonly", Bool(false)),
                Obj("name", Str("hunt.species"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("锁定物种：打中的那只有名字，之后优先按它找（附近还有同种就继续）")),
                Obj("name", Str("hunt.speciesArg"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("锁定是为哪句指令锁的；换指令就自动解锁")),
                Obj("name", Str("hunt.weapon"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("这一轮拿在手上的武器（Task.EquipWeapon 写）")),
                Obj("name", Str("hunt.killed"), "type", Str("bool"), "readonly", Bool(false),
                    "description", Str("上一只打死了没有（Task.Attack 按血量判定后写）")),
                Obj("name", Str("hunt.kills"), "type", Str("int"), "readonly", Bool(false),
                    "description", Str("这次指令累计打死几只（Task.HuntReport 维护）")),
                Obj("name", Str("hunt.result"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("回传给人的结果，如 killed Black Bull (total 2)")),
                // ---- 捡取包（`pickup.scbtpak`）的契约键：引用它就得声明 ----
                Obj("name", Str("pickup.target"), "type", Str("actor"), "readonly", Bool(false)),
                Obj("name", Str("pickup.targetIsSet"), "type", Str("bool"), "readonly", Bool(false)),
                Obj("name", Str("pickup.targetDistance"), "type", Str("float"), "readonly", Bool(false)),
                Obj("name", Str("pickup.targetName"), "type", Str("string"), "readonly", Bool(false)));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("demo.laya"),
                "name", Str("示例·Laya 驱动的主循环"),
                "entry", Str("root"),
                "blackboard", blackboard,
                "references", Arr(Obj("id", Str(PickupPackageId), "path", Str(PickupFile))));

            // 动作失败的入口：先记一行 → **连败计数 +1** → 再清标记。
            // 计数是"反射层兜底"的输入：摘要不变时模型会一直给同一个答案（实测连答 7 次 `sleep`，
            // 而该动作在当前局面不可能成功），而失败标记**不许进摘要**（G22），
            // 所以"连败几次了"只能活在树里（见 do_fallback / do_standby 两级）。
            PackageValue onActionFail = Node("on_action_fail", "Sequence",
                null,
                Arr(Obj("id", Str("d_action_fail"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    // `!= ""` 而不是 `IsSet`：缺键=没失败、空串=没失败、有内容=真失败（A28b）
                    "properties", Obj(
                        "key", Str("action.fail"),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("!="),
                        "value", Str(string.Empty)))),
                null,
                Arr(
                    Node("action_fail_log", "Task.Log", Obj(
                        "message", Str("demo.laya: the last action failed -> counting it and re-deciding"))),
                    Node("action_fail_count", "Task.Counter", Obj(
                        "key", Str(FailCountKey), "delta", Num(1))),
                    Node("action_fail_clear", "Task.SetBlackboard", Obj(
                        "key", Str("action.fail"), "valueKind", Str("string"),
                        "value", Str(string.Empty)))));

            var branches = new List<PackageValue>();
            for (int i = 0; i < LayaGoalScripts.Length; i++)
                branches.Add(GoalBranch(LayaGoalScripts[i][0], LayaGoalScripts[i][1]));
            branches.Add(Node("no_goal", "Task.Wait", Obj("seconds", Num(0.3))));

            PackageValue ask = Node("ask", "Task.LayaAsk", Obj(
                "questions", Str(QuestionBank.WorldGoalFileName),
                // **只问 `goal`**（2026-09-26 实测后从 `goal,threat,can_reach` 收窄到这里）。
                //
                // 为什么砍掉另外两个门：
                //   ① 它们**恒真** —— `threat`/`can_reach` 这类"断言式"问题在这个模型上
                //      无论状态是什么都答 `yes`（2 选项形态恒 yes、3 选项形态恒 none，
                //      与选项顺序也无关；PC 与平板逐条复现）。真实决策循环里也是每一轮
                //      `threat=yes can_reach=yes`（43/43）。
                //   ② 树**从来没用过**它们 —— 分支只按 `goal` 走，所以它们是"付了钱不用"。
                //   ③ 它们还占着请求头（每问一段 instructions + 选项文本）：实测基线
                //      **178 input tokens / 154 ms**，砍掉两问后应明显下降。
                // 于是"门"这件事整个折进 `goal` 的动作选项里（`fight`/`flee`/`eat`/`sleep` 本来就在），
                // 也就是 plan 那句"只问动作、不问属性"。
                "only", Str("goal"),
                "answerKeys", Str("goal:goal:str"),
                // 服务抖动/超时时**保留上次目标**继续跑（C6 的降级链：降级但不失控）；
                // 首次就失败时 goal 为空 → dispatch 落到 no_goal，什么都不做。
                "onUnavailable", Str("keep"),
                "writeFailKey", Bool(true),
                "failKey", Str("laya.fail"),
                "reasonKey", Str("laya.reason")));

            PackageValue decide = Node("decide", "Sequence",
                null,
                Arr(Obj("id", Str("d_cooldown"), "type", Str("Cooldown"),
                    "properties", Obj("cooldownSeconds", Num(2.5)))),
                null,
                Arr(
                    ask,
                    Node("dispatch", "Selector", null, null, null, Arr(branches.ToArray())),
                    Node("do", "Sequence",
                        null,
                        Arr(Obj("id", Str("d_has_script"), "type", Str("Blackboard"),
                            "observerAborts", Str("LowerPriority"),
                            "properties", Obj(
                                "key", Str("work.script"),
                                "query", Str("Compare"),
                                "valueKind", Str("string"),
                                "operator", Str("!="),
                                "value", Str(string.Empty)))),
                        null,
                        Arr(
                            Node("run", "Task.RunActionScript", Obj(
                                "scriptKey", Str("work.script"),
                                "writeFailKey", Bool(true),
                                "failKey", Str("action.fail"),
                                "reasonKey", Str("action.reason"))),
                            Node("clear_script", "Task.SetBlackboard", Obj(
                                "key", Str("work.script"), "valueKind", Str("string"),
                                "value", Str(string.Empty)))))));

            // ---- 反射层兜底：连败到阈值就不再问模型，改由 C# 确定性执行（两级阶梯）
            //
            // 为什么这么搭：模型在**摘要不变**时会重复同一个答案（实测 7 连 `sleep`），
            // 而"上一次失败了"这件事不许进摘要（G22 的红线），所以判据只能是树里的计数器。
            //   ① `>= 3` → 换一条**兜底脚本**（explore）：它**成功才清零** ——
            //      成功 → 回到正常判定（模型可以重新提案）；失败 → 计数继续涨，进第 ② 级；
            //   ② `>= 6` → **停手 5 秒**并清零：给世界（或人）一点时间，顺便把 token 花销打断。
            PackageValue fallback = Node("do_fallback", "Sequence",
                null,
                Arr(Obj("id", Str("d_fallback"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str(FailCountKey),
                        "query", Str("Compare"),
                        "valueKind", Str("int"),
                        "operator", Str(">="),
                        "value", Num(FallbackThreshold)))),
                null,
                Arr(
                    Node("fallback_log", "Task.Log", Obj(
                        "message", Str("demo.laya: " + FallbackThreshold
                            + " failures in a row -> running the fallback script instead of asking"))),
                    Node("fallback_pick", "Task.SetBlackboard", Obj(
                        "key", Str("work.script"), "valueKind", Str("string"),
                        "value", Str(ActionScriptNames.ExploreOnce))),
                    Node("fallback_run", "Task.RunActionScript", Obj(
                        "scriptKey", Str("work.script"),
                        "writeFailKey", Bool(true),
                        "failKey", Str("action.fail"),
                        "reasonKey", Str("action.reason"))),
                    // 清零放在**成功之后**：失败时这一支整条失败，计数留给下一级
                    Node("fallback_clear", "Task.Counter", Obj(
                        "key", Str(FailCountKey), "clear", Bool(true))),
                    Node("fallback_clear_script", "Task.SetBlackboard", Obj(
                        "key", Str("work.script"), "valueKind", Str("string"),
                        "value", Str(string.Empty)))));

            PackageValue standby = Node("do_standby", "Sequence",
                null,
                Arr(Obj("id", Str("d_standby"), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str(FailCountKey),
                        "query", Str("Compare"),
                        "valueKind", Str("int"),
                        "operator", Str(">="),
                        "value", Num(StandbyThreshold)))),
                null,
                Arr(
                    Node("standby_log", "Task.Log", Obj(
                        "message", Str("demo.laya: " + StandbyThreshold
                            + " failures in a row -> standing by for 5 s"))),
                    Node("standby_clear", "Task.Counter", Obj(
                        "key", Str(FailCountKey), "clear", Bool(true))),
                    Node("standby_wait", "Task.Wait", Obj("seconds", Num(5)))));

            // ---- 聊天优先分支（2026-09-26）：**放在根 Selector 最前面**。
            //
            // 为什么在最前面 + `observerAborts=LowerPriority`：有人叫它的时候不该等它挖完这一格
            // （`decide` 正在 InProgress 时，低优先级被抢占才会立刻停手去回应）。
            // 处理完（消费掉 `chat.pending`）自然回到原来的挖矿/判定循环 —— 这就是 §4.8 说的
            // "策略之间的无缝流程由原行为树包承担"。
            PackageValue chat = ChatBranch();

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(true)), null, null, Arr(
                Node("sel", "Selector", null, null, Arr(ChatWatchService()), Arr(
                    chat,
                    onActionFail,
                    standby,
                    fallback,
                    decide,
                    Node("idle", "Task.Wait", Obj("seconds", Num(0.3)))))));

            return new PackageTemplate(LayaDemoFile, "示例·Laya 驱动的主循环",
                "问一次 Laya（world_goal：goal）→ 按 goal 选一条出厂动作脚本 → 跑完再问。"
                + "模型只给枚举，脚本映射与时长/角度都在 C#（§4.9）；Cooldown 2.5 s 是决策节奏；"
                + "动作失败会写 action.fail 并由 on_action_fail 分支记账 + 清标记。",
                manifest, tree);
        }

        // ---------------------------------------------------------------- 聊天优先分支（2026-09-26）

        /// <summary>
        /// **聊天优先分支**：有人在聊天里说话 → 判定意图 → 执行 → 消费掉，然后回到原来的循环。
        ///
        /// 形状（四段，各自只做一件事）：
        ///
        ///     Sequence.chat      [decorator: chat.pending == true, observerAborts=LowerPriority]
        ///       services: ChatWatch(prefix=chat., targetKey=chat.target)
        ///       ├─ Selector  ask_or_skip
        ///       │    ├─ Sequence [chat.intent == ""] → Task.LayaAsk(chat_intent, digestKey=chat.digest)
        ///       │    └─ Task.Wait(0.01)                      ← 短语表已命中，不必问模型
        ///       ├─ Selector  dispatch                        ← 装饰器 observerAborts=Self
        ///       │    ├─ do_ignore / do_stop / do_follow / do_come（都要 chat.targetIsSet）
        ///       │    └─ 兜底：记一行"判不出意图"
        ///       └─ Sequence  consume：清 intent / bank / digest / pending
        ///
        /// 三个"为什么"：
        ///   · **`chat.pending` 单独立一个键**：兜底路径下 `chat.intent` 是空的（要先去问模型），
        ///     "空"不能同时表示"没事"和"待问"；
        ///   · **意图装饰器 `observerAborts=Self`**：这样"跟着我"跑到一半收到"停"能**立刻**
        ///     中断那一支（`Task.FollowEntity` 的 OnExit 会松开移动键）；
        ///   · **要不要目标由装饰器把住**：`chat.targetIsSet == true` 才让 come/follow 起跑，
        ///     否则会掉进"每 tick 起一次必失败的跟随"（日志刷屏、动作全废）。
        /// </summary>
        private static PackageValue ChatBranch()
        {
            // ① 兜底：短语表没命中 → 拿"那句话"问一次模型。
            //    `onUnavailable=default` + `ignore`：模型不可用时**宁可不动**，也不要瞎跑。
            PackageValue ask = Node("chat_ask", "Task.LayaAsk", Obj(
                "questions", Str(QuestionBank.ChatIntentFileName),
                "digestKey", Str("chat.digest"),
                "answerKeys", Str("chat.intent:chat_intent:str"),
                "only", Str("chat_intent"),
                "onUnavailable", Str("default"),
                "defaultValue", Str(ChatIntents.Ignore),
                "writeFailKey", Bool(false)));

            PackageValue askOrSkip = Node("chat_ask_or_skip", "Selector", null, null, null, Arr(
                Node("chat_ask_branch", "Sequence", null,
                    Arr(CompareDecorator("d_chat_needs_ask", "chat.intent", "string", "==",
                        Str(string.Empty), "None")),
                    null, Arr(ask)),
                Node("chat_ask_skip", "Task.Wait", Obj("seconds", Num(0.01)))));

            // ② 意图分派。注意顺序：`ignore` 在最前（它是"别理我"），到不了的分支自然跳过。
            var dispatchChildren = new List<PackageValue>
            {
                ChatIntentBranch(ChatIntents.Ignore, "ignore (not an instruction for it)", false),
                ChatIntentBranch(ChatIntents.Stop, "stop -> stand still", false),
                // 打猎排在 stop 之后（"别打" 已被 stop 用否定词吃掉）、follow/come 之前
                ChatHuntBranch(),
                ChatIntentBranch(ChatIntents.Follow, "follow -> keep following the speaker", true),
                ChatIntentBranch(ChatIntents.Come, "come -> walk to the speaker and stop near them", true),
                // 兜底：模型答了闭集之外的 key（理论上被 answerKeys 的类型校验挡住，
                // 但那一步失败会走 onUnavailable=default → ignore；这里只是"绝不让整支卡住"）
                Node("chat_unknown", "Sequence", null, null, null, Arr(
                    Node("chat_unknown_log", "Task.Log", Obj(
                        "message", Str("demo.laya: chat -> could not tell what it wants; ignoring")))))
            };
            PackageValue dispatch = Node("chat_dispatch", "Selector", null, null, null,
                Arr(dispatchChildren.ToArray()));

            // ③ 消费掉这一次指令（顺序无所谓，但 `pending` 必须最后清）。
            PackageValue consume = Node("chat_consume", "Sequence", null, null, null, Arr(
                Node("chat_clear_intent", "Task.SetBlackboard", Obj(
                    "key", Str("chat.intent"), "valueKind", Str("string"), "value", Str(string.Empty))),
                Node("chat_clear_bank", "Task.SetBlackboard", Obj(
                    "key", Str("chat.bank"), "valueKind", Str("string"), "value", Str(string.Empty))),
                Node("chat_clear_digest", "Task.SetBlackboard", Obj(
                    "key", Str("chat.digest"), "valueKind", Str("string"), "value", Str(string.Empty))),
                // 打猎的参数与"锁定物种"也要清：换一条指令就该换物种，
                // 否则下一句"打狼"会接着打上一句锁定的牛
                Node("chat_clear_arg", "Task.SetBlackboard", Obj(
                    "key", Str("chat.arg"), "valueKind", Str("string"), "value", Str(string.Empty))),
                Node("chat_clear_species", "Task.SetBlackboard", Obj(
                    "key", Str("hunt.species"), "valueKind", Str("string"), "value", Str(string.Empty))),
                Node("chat_clear_species_arg", "Task.SetBlackboard", Obj(
                    "key", Str("hunt.speciesArg"), "valueKind", Str("string"), "value", Str(string.Empty))),
                Node("chat_clear_pending", "Task.SetBlackboard", Obj(
                    "key", Str("chat.pending"), "valueKind", Str("bool"), "value", Bool(false)))));

            return Node("chat", "Sequence",
                null,
                Arr(CompareDecorator("d_chat_pending", "chat.pending", "bool", "==", Bool(true),
                    "LowerPriority")),
                // ⚠️ **服务不挂在这里**（挂在根本没有激活的分支上就不会 tick —— 这是实测踩到的：
                //    "服务只在活动路径上跑"，于是 `chat.pending` 永远不被写、这一支永远进不来）。
                //    它挂在根 Selector 上，与 `UpdateNearestPlayer` / `Service.ObserveState` 同一个位置。
                null,
                Arr(askOrSkip, dispatch, consume));
        }

        /// <summary>
        /// **聊天监听服务**的节点定义（挂在**根 Selector** 上，不是挂在聊天分支上）。
        ///
        /// 为什么位置这么重要：行为树的服务**只在活动路径上 tick**。挂在 `chat` 分支上时，
        /// 该分支的守卫条件（`chat.pending == true`）恰恰要靠这个服务去写 ——
        /// 于是"没写 → 分支不激活 → 服务不跑 → 没写"的**死锁**（2026-09-26 实测踩到：
        /// `dev.chat.inject` 注入成功、黑板里却什么都没有）。
        /// </summary>
        private static PackageValue ChatWatchService()
        {
            // ⚠️ **必须包在 `properties` 里**：包的**服务**格式是
            // `{id, type, interval?, randomDeviation?, tickOnActivation?, properties:{…}}`
            // （`ScbtTree.ParseService` 只消费这几个字段、`TreeCompiler` 也是拿 `doc.Properties`
            //  再逐字段读）。`ServiceEntry` 是"把 body 的成员摊到服务对象上"，
            // 所以把参数摊平写会变成**顶层** `prefix`/`targetKey`/… —— 校验器当场报
            // `unknown field 'prefix' (ignored; check spelling)`，属性也被静默忽略（走默认值）。
            // 2026-09-26 实测踩到（`PackageSelfTest` 的"出厂包零告警"那条把它逮住了）。
            return ServiceEntry("svc_chat", "ChatWatch", Obj("properties", Obj(
                "prefix", Str("chat."),
                "targetKey", Str("chat.target"),
                "useFallback", Bool(true),
                "fallbackBank", Str(QuestionBank.ChatIntentFileName.Replace(".qbank", string.Empty)),
                "clearWhenMissing", Bool(true))));
        }

        /// <summary>
        /// 一支意图分支：`chat.intent == &lt;intent&gt;` →（可选要求有目标）→ 记一行 → 执行。
        /// 装饰器用 `observerAborts=Self`：意图一变就中断这一支（"停"能打断"跟着我"）。
        /// </summary>
        private static PackageValue ChatIntentBranch(string intent, string note, bool requireTarget)
        {
            var decorators = new List<PackageValue>
            {
                CompareDecorator("d_chat_" + intent, "chat.intent", "string", "==", Str(intent), "Self")
            };
            if (requireTarget)
            {
                decorators.Add(CompareDecorator("d_chat_" + intent + "_target", "chat.targetIsSet",
                    "bool", "==", Bool(true), "Self"));
            }

            var children = new List<PackageValue>
            {
                Node("chat_" + intent + "_log", "Task.Log", Obj(
                    "message", Str("demo.laya: chat -> " + note)))
            };
            if (string.Equals(intent, ChatIntents.Come, StringComparison.Ordinal))
            {
                // **一次性到达**：走到 2 格内就 Success（这一支做完就回去干原来的事）
                children.Add(Node("chat_come_go", "Task.FollowEntity", Obj(
                    "targetKey", Str("chat.target"),
                    "keepDistance", Num(2f),
                    "arriveThenSucceed", Bool(true),
                    "timeout", Num(30f))));
            }
            else if (string.Equals(intent, ChatIntents.Follow, StringComparison.Ordinal))
            {
                // 持续跟随：没有超时，直到收到"停"（靠装饰器的 Self 中断让位）
                children.Add(Node("chat_follow_go", "Task.FollowEntity", Obj(
                    "targetKey", Str("chat.target"),
                    "keepDistance", Num(2f),
                    "timeout", Num(0f))));
            }
            else if (string.Equals(intent, ChatIntents.Stop, StringComparison.Ordinal))
            {
                // "停"不需要额外动作：上一支（跟随/挖矿）被中断时 OnExit 已经松开了输入。
                // 这里只是**占住一拍**，让人看得出它确实停下来了（日志 + 短暂等待）。
                children.Add(Node("chat_stop_wait", "Task.Wait", Obj("seconds", Num(1))));
            }
            // ignore：只有日志，什么都不做（这一支成功结束 → 消费 → 回到原循环）

            return Node("chat_do_" + intent, "Sequence", null,
                Arr(decorators.ToArray()), null, Arr(children.ToArray()));
        }

        // ---------------------------------------------------------------- 打猎分支（2026-09-26）

        /// <summary>打猎时找生物的半径（米）：牛群一般就在视野里，48 格够用且不会跨半张地图。</summary>
        public const float HuntRadius = 48f;

        /// <summary>
        /// 靠近目标的超时（秒）。
        ///
        /// 攻击步骤只会"闷头按 W"（没有寻路），所以**走远路必须由这一步的 A* 负责**：
        /// 20 米外只给 8 秒的话，会在半路被地形卡住（2026-09-26 实测停在 16 米不动）。
        /// 到了 2 格内它就 Success（不白等），所以给足 30 秒不亏。
        /// </summary>
        public const float HuntApproachTimeout = 30f;

        /// <summary>
        /// 攻击时"够得着"的判定距离（米）。
        ///
        /// ⚠️ **近战其实只有 2 米**：`ComponentPlayer.cs:237` 只有在
        /// `Distance(命中点, 眼睛) &lt;= 2f` 时才真的调 `ComponentMiner.Hit(...)` ——
        /// 这是**命中点**到眼睛的距离，不是两个身体中心的距离，所以站位要比 2 米再近一点点。
        /// 2026-09-26 实测：站在 3.0 米能打死牛，站在 4.4 米**一刀都不落**（一直空挥、动物掉血为 0）。
        /// 取 2.5 米：默认的 `KeepDistance=2` 把身体带到 ~2 米，这个值只负责"再往前贴一点"。
        /// </summary>
        public const float HuntAttackRange = 2.5f;

        /// <summary>
        /// 打猎时的连点间隔（秒）。
        ///
        /// **必须大于引擎的攻击冷却 0.66 秒**（`ComponentMiner.cs:295`）—— 冷却内的挥击会被
        /// **整个丢掉**（不是少打一点）。默认值 0.6 正好踩在这个坑里：动物血量恒定不掉，
        /// 看着像"没打中"，其实是"一次都没生效"（2026-09-26 实测）。
        /// 0.75 秒留了 0.09 秒余量，按 60 fps 也经得起一帧抖动。
        /// </summary>
        public const float HuntClickInterval = 0.75f;

        /// <summary>
        /// 整场打猎的上限（秒）。**随时可被打断**：新来一条聊天指令会靠
        /// `d_chat_hunt` 的 `observerAborts=Self` 当场中断这一支（"停"一句话就能收手）。
        ///
        /// ⚠️ 注意这里**没有**"单只打多久"的预算了：用户 2026-09-26 明确要求
        /// "不需要设置打死的时间，只需要检查动物是否打死" —— 于是 `Task.Attack` 用
        /// `timeout=0`（不限时）+ 血量判定（`healthProbe` + `killedKey`），
        /// 一只没打死就一直打。这个总上限只是"别把一整局都耗在一件事上"的保险。
        /// </summary>
        public const float HuntLoopTimeout = 600f;

        /// <summary>
        /// **打猎分支**：`chat.intent == hunt` → 按名字找最近的那只 → 靠近 → 打死 → 捡掉落 →
        /// 附近还有同种就继续，打完回主循环。
        ///
        /// 四个"为什么"：
        ///   · **物种词由 C# 表解析**（`animals.json` → `ChatAnimalAliases.Expand` → 按名找生物），
        ///     不让模型编物种 —— 模型答一个树里没有的动物就是"答了却什么都不发生"（§4.7）；
        ///   · **`chat.arg` 为空就不进这一支**（装饰器把门）："帮我打一下"没说打什么时宁可什么都不做，
        ///     也不能拿"最近的任意生物"顶替 —— 那可能是一只熊；
        ///   · **服务挂在这一支的 Sequence 上、Loop 之外**：它要在整个打猎期间持续刷新目标；
        ///   · **`ForceSuccess` 包住 `Loop`**：附近压根没有那种动物时 Loop 会判 `Failed`，
        ///     而"没得打"不算执行失败 —— 否则分派 Selector 会掉到"判不出意图"那条兜底日志上（误导）。
        /// </summary>
        private static PackageValue ChatHuntBranch()
        {
            PackageValue update = ServiceEntry("svc_hunt", "UpdateNearestCreature", Obj("properties", Obj(
                "targetKey", Str("hunt.target"),
                // 按名找：候选词来自 `Service.ChatWatch` 抠出来的 `chat.arg`
                "nameKey", Str("chat.arg"),
                // 打中第一只后把它的名字锁在这里 → 下一轮还是同一种（"附近还有同种就继续"）
                "speciesKey", Str("hunt.species"),
                "maxDistance", Num(HuntRadius),
                "clearWhenMissing", Bool(true))));

            // 一轮 = 有目标才动（没目标就失败 → 结束循环）→ 换武器 → 靠近 → 打死 → 回传
            PackageValue round = Node("hunt_round", "Sequence", null,
                Arr(CompareDecorator("d_hunt_has_target", "hunt.targetIsSet", "bool", "==",
                    Bool(true), "None")),
                null,
                Arr(
                    Node("hunt_kill", "Sequence", null, null, null, Arr(
                        // **先换武器再打**：空手/拿方块打大牲口几乎打不动（用户 2026-09-26 要求）。
                        // `required=false`：拿不到刀也照打，只是慢（日志里会写清为什么）
                        Node("hunt_weapon", "Task.EquipWeapon", Obj(
                            "priority", Str(PlayerEquipment.DefaultPriority),
                            "allowCreativeGrab", Bool(true),
                            "weaponKey", Str("hunt.weapon"),
                            "required", Bool(false))),
                        // 靠近：A* 寻路把自己带到 2 格内（**到了就算成功**，别白等超时）。
                        // ⚠️ 必须套 `ForceSuccess`：`FollowEntity` 到点会判 **Failed**，
                        //    而 Sequence 里一个 Failed 就会**中断后面所有步骤** ——
                        //    于是攻击步骤永远轮不到，表现是"每 8 秒重开一轮、一次都没挥"（实测踩到）。
                        // ⚠️ 超时也要够长：攻击步骤只会"闷头按 W"（没有寻路），
                        //    20 米外光靠它会被地形卡住（实测停在 16 米不动）—— 走远路由这一步负责。
                        Node("hunt_approach", "Task.FollowEntity", Obj(
                            "targetKey", Str("hunt.target"),
                            "keepDistance", Num(2f),
                            "arriveThenSucceed", Bool(true),
                            "timeout", Num(HuntApproachTimeout)),
                            Arr(ForceSuccessDecorator("d_hunt_approach_soft")), null, null),
                        // **不限时**（`timeout=0`）：打死与否看血量，不看钟表 —— 用户明确要求。
                        // `clickInterval=0.75` 必须**大于引擎 0.66 秒的攻击冷却**（`ComponentMiner.cs:295`），
                        // 否则每次挥击都被冷却丢掉、伤害恒为 0（2026-09-26 实测踩到，见 §9.5.53）
                        Node("hunt_attack", "Task.Attack", Obj(
                            "targetKey", Str("hunt.target"),
                            "range", Num(HuntAttackRange),
                            "timeout", Num(0f),
                            "clickInterval", Num(HuntClickInterval),
                            "healthProbe", Bool(true),
                            "killedKey", Str("hunt.killed"))))),
                    // 回传：把"打死没有"落成 hunt.result / hunt.kills + 事件日志
                    Node("hunt_report", "Task.HuntReport", Obj(
                        "killedKey", Str("hunt.killed"),
                        "speciesKey", Str("hunt.species"),
                        "countKey", Str("hunt.kills"),
                        "resultKey", Str("hunt.result"),
                        "summary", Bool(false))),
                    // 打完捡掉落：调**独立的捡取包**（不在这里重写一遍"走过去"）
                    Node("hunt_loot", "Task.Subtree", Obj(
                        "package", Str(PickupPackageId + "#root")))));

            return Node("chat_do_hunt", "Sequence", null,
                Arr(
                    CompareDecorator("d_chat_hunt", "chat.intent", "string", "==",
                        Str(ChatIntents.Hunt), "Self"),
                    CompareDecorator("d_chat_hunt_arg", "chat.arg", "string", "!=",
                        Str(string.Empty), "Self")),
                Arr(update),
                Arr(
                    Node("chat_hunt_log", "Task.Log", Obj(
                        "message", Str("demo.laya: chat -> hunt the named animal (nearest first, "
                            + "keep going while the same species is around)"))),
                    Node("chat_hunt_repeat", "Sequence", null,
                        // ⚠️ **顺序有讲究：`ForceSuccess` 在前、`Loop` 在后** —— 装饰器数组是
                        //    "外 → 内"，`ModifyResult` 由**内向外**跑。写反了（Loop 在外、ForceSuccess 在内）
                        //    会让"没目标"的 Failed 先在里层被改成 Succeeded，Loop 便以为这一轮成功了：
                        //    于是"附近没有那种动物"时它空转整整 10 分钟、**一条回传都不发**
                        //    （2026-09-26 实测：activePath 停在 chat_do_hunt 不动）。
                        //    现在：没目标 → 里层 Loop 收到 Failed 就停 → 外层 ForceSuccess 收成 Succeeded
                        //    → 继续走下面的总结回传。
                        Arr(ForceSuccessDecorator("d_hunt_soft"),
                            LoopForeverDecorator("d_hunt_loop", HuntLoopTimeout)),
                        null,
                        Arr(round)),
                    // 收尾回传：打了几只、有没有打空，落成一句话给人和日志看
                    Node("chat_hunt_summary", "Task.HuntReport", Obj(
                        "killedKey", Str("hunt.killed"),
                        "speciesKey", Str("hunt.species"),
                        "countKey", Str("hunt.kills"),
                        "resultKey", Str("hunt.result"),
                        "summary", Bool(true))),
                    Node("chat_hunt_done", "Task.Log", Obj(
                        "message", Str("demo.laya: hunt finished (see hunt.result / hunt.kills / hunt.weapon)")))));
        }

        /// <summary>`Loop` 装饰器：把子树反复跑，直到它失败或到时间上限（防止无限打下去）。</summary>
        private static PackageValue LoopForeverDecorator(string id, float timeoutSeconds)
        {
            return Obj("id", Str(id), "type", Str("Loop"),
                "observerAborts", Str("None"),
                "properties", Obj(
                    "numLoops", Num(1),
                    "infiniteLoop", Bool(true),
                    "infiniteLoopTimeoutSeconds", Num(timeoutSeconds)));
        }

        /// <summary>`ForceSuccess`：把 `Failed` 变 `Succeeded`（"没得打"不是执行失败）。</summary>
        private static PackageValue ForceSuccessDecorator(string id)
        {
            return Obj("id", Str(id), "type", Str("ForceSuccess"),
                "observerAborts", Str("None"), "properties", Obj());
        }

        /// <summary>`Blackboard` 比较装饰器（`query=Compare`）——聊天分支里用得最多，抽出来免得抄错。</summary>
        private static PackageValue CompareDecorator(string id, string key, string valueKind,
            string op, PackageValue value, string abortMode)
        {
            return Obj("id", Str(id), "type", Str("Blackboard"),
                "observerAborts", Str(abortMode),
                "properties", Obj(
                    "key", Str(key),
                    "query", Str("Compare"),
                    "valueKind", Str(valueKind),
                    "operator", Str(op),
                    "value", value));
        }

        /// <summary>
        /// 一个目标分支：`goal == <answer>` → 写 `work.script`（+ 记一行日志）。
        /// 只写脚本名、不在这里跑：跑动作的是后面共用的 `Task.RunActionScript`，
        /// 于是"八个目标共用一条执行路径"，加目标只加一支。
        /// </summary>
        private static PackageValue GoalBranch(string goal, string script)
        {
            return Node("go_" + goal, "Sequence",
                null,
                Arr(Obj("id", Str("d_goal_" + goal), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str("goal"),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("=="),
                        "value", Str(goal)))),
                null,
                Arr(
                    Node("pick_" + goal, "Task.SetBlackboard", Obj(
                        "key", Str("work.script"), "valueKind", Str("string"), "value", Str(script))),
                    Node("log_" + goal, "Task.Log", Obj(
                        "message", Str("demo.laya: goal=" + goal + " -> " + script)))));
        }

        // ---------------------------------------------------------------- 世界外界面链示例

        /// <summary>
        /// 一个界面分支：`front.goal == <answer>` → 记一行 → 点一个语义目标。
        /// `target` 为空时只记日志 + 等一下（**明说是占位**，不假装点过）。
        /// </summary>
        private static PackageValue FrontBranch(string id, string answer, string target, string note,
            float waitSeconds)
        {
            var children = new List<PackageValue>
            {
                Node(id + "_log", "Task.Log", Obj("message", Str("demo.front: goal=" + answer + " -> " + note)))
            };
            if (!string.IsNullOrEmpty(target))
            {
                children.Add(Node(id + "_click", "Task.UiClick", Obj(
                    "target", Str(target),
                    "mode", Str("direct"),
                    "waitSeconds", Num(waitSeconds),
                    "repeat", Num(1))));
            }
            else
            {
                children.Add(Node(id + "_wait", "Task.Wait", Obj("seconds", Num(0.4))));
            }

            return Node(id, "Sequence",
                null,
                Arr(Obj("id", Str("d_" + id), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str("front.goal"),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("=="),
                        "value", Str(answer)))),
                null,
                Arr(children.ToArray()));
        }

        /// <summary>
        /// `open_ui` 的**屏幕分叉**（G20 说的"固定链条 + 只在分叉点判定"的执行部分）：
        /// 同一个"按下主按钮"的意思，在选档界面上要**先选中一行** —— 否则 `Play` 点下去什么都不会发生
        /// （实测：主菜单 → 选档界面之后，光点 `Play` 会每 2 秒空点一次，永远进不去）。
        ///
        /// 选第几行是 **C# 的确定性取值**（这里固定第 0 行，"选上次玩的世界"改这一支的目标即可）：
        /// 模型只说"按下主按钮"，点哪一行不该由它给坐标（§4.9）。
        /// </summary>
        private static PackageValue FrontOpenBranch(string id, string screen, string note,
            bool selectRow, float waitSeconds)
        {
            var children = new List<PackageValue>
            {
                Node(id + "_log", "Task.Log", Obj("message", Str("demo.front: open_ui on " + screen + " -> " + note)))
            };
            if (selectRow)
            {
                children.Add(Node(id + "_row", "Task.UiClick", Obj(
                    "target", Str(FrontWorldRowTarget),
                    "mode", Str("direct"),
                    "waitSeconds", Num(waitSeconds),
                    "repeat", Num(1))));
                children.Add(Node(id + "_settle", "Task.Wait", Obj("seconds", Num(0.3))));
            }
            children.Add(Node(id + "_click", "Task.UiClick", Obj(
                "target", Str("Play"),
                "mode", Str("direct"),
                "waitSeconds", Num(waitSeconds),
                "repeat", Num(1))));

            return Node(id, "Sequence",
                null,
                Arr(Obj("id", Str("d_" + id), "type", Str("Blackboard"),
                    "observerAborts", Str("LowerPriority"),
                    "properties", Obj(
                        "key", Str("state.scr"),
                        "query", Str("Compare"),
                        "valueKind", Str("string"),
                        "operator", Str("=="),
                        "value", Str(screen)))),
                null,
                Arr(children.ToArray()));
        }

        /// <summary>选档界面里"第 0 行世界"的语义目标（列表行选择器，坐标由游戏端在点击那一刻现算）。</summary>
        public const string FrontWorldRowTarget = "list:WorldsList#0";

        /// <summary>
        /// **示例·世界外界面链**（`demo.front.scbtpak`）—— §4.13 的"世界外那一套"，
        /// 也是 G20 说的"世界外操作链的配方"。
        ///
        /// <code>
        /// Root(loop=true)   services: ObserveState(prefix=state., interval=0.2)   ← 状态进黑板
        /// └─ Selector.sel
        ///    ├─ Sequence.step  [Blackboard(state.phase == "front") + Cooldown 2.0]
        ///    │  ├─ Task.LayaAsk  questions=front_goal  only=front_goal  answerKeys=front.goal:front_goal:str
        ///    │  ├─ Selector.act
        ///    │  │  ├─ Sequence.do_open   [front.goal == "open_ui"] → Task.UiClick Play
        ///    │  │  ├─ Sequence.do_back   [front.goal == "back"]    → Task.UiClick TopBar.Back
        ///    │  │  ├─ Sequence.do_wait   [front.goal == "wait"]    → 等 0.4 s
        ///    │  │  └─ Sequence.do_cancel [front.goal == "cancel"]  → 只记一行（占位）
        ///    │  └─ Task.SetBlackboard front.goal = ""            ← 消费掉答案
        ///    └─ Task.Wait 0.5
        /// </code>
        ///
        /// **三条不变量**：
        ///   · **正条件守卫**（`state.phase == "front"`，而不是 `!= "world"`）：拿不到状态时**什么都不做** ——
        ///     世界外点错按钮的代价是"进了别的世界/删了档"，所以这里必须 fail-safe；
        ///   · **状态来自 `Service.ObserveState`**：`phase` 是事实查询，问模型既慢又可能答错；
        ///     这条也是"世界内/世界外两棵树同池共存"的实现方式 —— 各自按 `state.phase` 决定这一拍该不该动
        ///     （D9：不新增换树来源）；
        ///   · **答案消费掉**：跑完一支就清 `front.goal`，否则 Laya 一次"open_ui"会在下一轮被当成新决定（连点）。
        ///
        /// 主按钮的名字在**主菜单**（`MainMenuScreen`）与**选档界面**（`SuPlayScreen`）里都叫 `Play` ——
        /// 同一条 `Task.UiClick target=Play` 就够走完"主菜单 → 选档 → 开始游戏"这条固定链；
        /// `back` 只认 `TopBar.Back`（选择器是**精确匹配**，`Back` 匹配不到它，见 `UiInspector.Resolve`）。
        /// </summary>
        private static PackageTemplate BuildFrontDemo()
        {
            PackageValue blackboard = Arr(
                Obj("name", Str("state.phase"), "type", Str("string"), "readonly", Bool(true),
                    "description", Str("Service.ObserveState 写：front / loading / world（世界外才动界面）")),
                Obj("name", Str("state.ui"), "type", Str("string"), "readonly", Bool(true),
                    "description", Str("Service.ObserveState 写：menu / hud / dialog / 面板名")),
                Obj("name", Str("state.scr"), "type", Str("string"), "readonly", Bool(true),
                    "description", Str("Service.ObserveState 写：当前屏幕名（MainMenu / Play / GameScreen…）")),
                Obj("name", Str("front.goal"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("Laya 判定这一步在界面上做什么（front_goal 的选项 key）")),
                Obj("name", Str("laya.fail"), "type", Str("string"), "readonly", Bool(false),
                    "description", Str("Laya 拿不到答案时的错误码（onUnavailable=fail 时什么都不做）")),
                Obj("name", Str("laya.reason"), "type", Str("string"), "readonly", Bool(false)));

            PackageValue manifest = Obj(
                "format", Str(ScbtManifest.FormatName),
                "version", Num(ScbtManifest.FormatVersion),
                "id", Str("demo.front"),
                "name", Str("示例·世界外界面链"),
                "entry", Str("root"),
                "blackboard", blackboard,
                "references", Arr());

            PackageValue ask = Node("ask", "Task.LayaAsk", Obj(
                "questions", Str(QuestionBank.FrontGoalFileName),
                "only", Str("front_goal"),
                "answerKeys", Str("front.goal:front_goal:str"),
                // 世界外**不做降级猜测**：拿不到答案就什么都不点（默认 `fail`）。
                // `keep` 在世界内是"保留上次目标继续干活"，在界面上会变成"对着同一颗按钮连点"。
                "onUnavailable", Str("fail"),
                "writeFailKey", Bool(true),
                "failKey", Str("laya.fail"),
                "reasonKey", Str("laya.reason")));

            // `open_ui` 不再是一步点击：主菜单与选档界面的"主按钮"虽然同名（`Play`），
            // 但选档界面**必须先选中一行**（见 FrontOpenBranch 的注释）。分叉键是 `state.scr`。
            PackageValue openUi = Node("do_open_ui", "Selector", null, null, null, Arr(
                FrontOpenBranch("open_main_menu", "MainMenu", "clicking Play", false, 3f),
                FrontOpenBranch("open_world_list", "Play", "selecting the first world, then Play", true, 3f),
                // 其它界面（例如多了一层的子屏）：只按"主按钮"这一层意思走，不猜
                Node("open_other", "Sequence",
                    null,
                    Arr(Obj("id", Str("d_open_other"), "type", Str("Blackboard"),
                        "observerAborts", Str("LowerPriority"),
                        "properties", Obj(
                            "key", Str("state.scr"),
                            "query", Str("IsSet")))),
                    null,
                    Arr(
                        Node("open_other_log", "Task.Log", Obj(
                            "message", Str("demo.front: open_ui on an unknown screen -> clicking Play"))),
                        Node("open_other_click", "Task.UiClick", Obj(
                            "target", Str("Play"),
                            "mode", Str("direct"),
                            "waitSeconds", Num(3f),
                            "repeat", Num(1)))))));

            PackageValue act = Node("act", "Selector", null, null, null, Arr(
                openUi,

                FrontBranch("do_back", "back", "TopBar.Back", "clicking TopBar.Back", 2f),
                FrontBranch("do_wait", "wait", null, "waiting (the screen is still animating)", 0f),
                FrontBranch("do_cancel", "cancel", null, "cancel is a placeholder (no click yet)", 0f)));

            PackageValue step = Node("step", "Sequence",
                null,
                Arr(
                    // 正条件：只有确认"在世界外"才动手（拿不到状态 = 不动）
                    Obj("id", Str("d_front"), "type", Str("Blackboard"),
                        "observerAborts", Str("LowerPriority"),
                        "properties", Obj(
                            "key", Str("state.phase"),
                            "query", Str("Compare"),
                            "valueKind", Str("string"),
                            "operator", Str("=="),
                            "value", Str("front"))),
                    Obj("id", Str("d_cooldown"), "type", Str("Cooldown"),
                        "properties", Obj("cooldownSeconds", Num(2.0)))),
                null,
                Arr(
                    ask,
                    act,
                    Node("clear_goal", "Task.SetBlackboard", Obj(
                        "key", Str("front.goal"), "valueKind", Str("string"),
                        "value", Str(string.Empty)))));

            PackageValue observe = ServiceEntry("svc_state", "Service.ObserveState", Obj(
                "interval", Num(0.2),
                "properties", Obj(
                    "prefix", Str("state."),
                    "only", Str(string.Empty),
                    "clearWhenMissing", Bool(true))));

            PackageValue tree = Node("root", "Root", Obj("loop", Bool(true)),
                null,
                Arr(observe),
                Arr(
                    Node("sel", "Selector", null, null, null, Arr(
                        step,
                        Node("idle", "Task.Wait", Obj("seconds", Num(0.5)))))));

            return new PackageTemplate(FrontDemoFile, "示例·世界外界面链",
                "世界外（phase=front）问 Laya 这一步在界面上做什么，再把答案翻成一个语义点击"
                + "（Play / TopBar.Back）；状态由 Service.ObserveState 写进黑板，世界里这条树自动待机。"
                + "这是 §4.13 的「世界外那套状态机」，也是 G20 的界面链配方。",
                manifest, tree);
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

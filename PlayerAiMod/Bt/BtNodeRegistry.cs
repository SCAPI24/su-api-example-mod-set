using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 节点类型注册表：**包格式、校验器、编译器和编辑器共用的单一事实来源**。
    ///
    /// 每个类型一次注册三件事（见 doc/player-ai-plan.md §4.1）：
    ///   1. 工厂：怎么造出对象；
    ///   2. 形状（<see cref="BtNodeShape"/>）：能不能有 children / services；
    ///   3. 属性表（<see cref="BtPropertySpec"/>）：有哪些参数、什么类型、哪些必填。
    ///
    /// 包里的节点只写类型标识（例如 `"type": "Selector"` / `"Task.Wait"`），
    /// 加载器（P0-4）按表造对象、按表读属性；校验器（P0-3）按表报错；
    /// 编辑器物料区也读这张表 —— 三处不会各自漂移。
    /// </summary>
    public static class BtNodeRegistry
    {
        private static readonly Dictionary<string, BtNodeInfo> s_nodes =
            new Dictionary<string, BtNodeInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, BtDecoratorInfo> s_decorators =
            new Dictionary<string, BtDecoratorInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, BtServiceInfo> s_services =
            new Dictionary<string, BtServiceInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_gate = new object();
        private static bool s_initialized;

        public static void EnsureInitialized()
        {
            lock (s_gate)
            {
                if (s_initialized)
                    return;
                s_initialized = true;
                RegisterBuiltIns();
            }
        }

        // ---------------------------------------------------------------- 注册

        public static void RegisterNode(string typeId, Func<BtNode> factory,
            BtNodeShape shape = BtNodeShape.Task, bool packageSerializable = true,
            params BtPropertySpec[] properties)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(typeId) || factory == null)
                throw new ArgumentException("Node type id and factory are required.");
            lock (s_gate)
            {
                s_nodes[typeId] = new BtNodeInfo(typeId, factory, shape, packageSerializable, properties);
            }
        }

        public static void RegisterDecorator(string typeId, Func<BtDecorator> factory,
            bool packageSerializable = true, params BtPropertySpec[] properties)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(typeId) || factory == null)
                throw new ArgumentException("Decorator type id and factory are required.");
            lock (s_gate)
            {
                s_decorators[typeId] = new BtDecoratorInfo(typeId, factory, packageSerializable, properties);
            }
        }

        public static void RegisterService(string typeId, Func<BtService> factory,
            bool packageSerializable = true, params BtPropertySpec[] properties)
        {
            EnsureInitialized();
            if (string.IsNullOrEmpty(typeId) || factory == null)
                throw new ArgumentException("Service type id and factory are required.");
            lock (s_gate)
            {
                s_services[typeId] = new BtServiceInfo(typeId, factory, packageSerializable, properties);
            }
        }

        // ---------------------------------------------------------------- 造对象

        public static bool TryCreateNode(string typeId, out BtNode node)
        {
            EnsureInitialized();
            node = null;
            BtNodeInfo info;
            if (!TryGetNodeInfo(typeId, out info))
                return false;

            node = info.Factory();
            return node != null;
        }

        public static bool TryCreateDecorator(string typeId, out BtDecorator decorator)
        {
            EnsureInitialized();
            decorator = null;
            BtDecoratorInfo info;
            if (!TryGetDecoratorInfo(typeId, out info))
                return false;

            decorator = info.Factory();
            return decorator != null;
        }

        public static bool TryCreateService(string typeId, out BtService service)
        {
            EnsureInitialized();
            service = null;
            BtServiceInfo info;
            if (!TryGetServiceInfo(typeId, out info))
                return false;

            service = info.Factory();
            return service != null;
        }

        // ---------------------------------------------------------------- 查表

        public static bool TryGetNodeInfo(string typeId, out BtNodeInfo info)
        {
            EnsureInitialized();
            info = null;
            if (string.IsNullOrEmpty(typeId))
                return false;
            lock (s_gate)
            {
                return s_nodes.TryGetValue(typeId, out info);
            }
        }

        public static bool TryGetDecoratorInfo(string typeId, out BtDecoratorInfo info)
        {
            EnsureInitialized();
            info = null;
            if (string.IsNullOrEmpty(typeId))
                return false;
            lock (s_gate)
            {
                return s_decorators.TryGetValue(typeId, out info);
            }
        }

        public static bool TryGetServiceInfo(string typeId, out BtServiceInfo info)
        {
            EnsureInitialized();
            info = null;
            if (string.IsNullOrEmpty(typeId))
                return false;
            lock (s_gate)
            {
                return s_services.TryGetValue(typeId, out info);
            }
        }

        /// <summary>
        /// 类型标识归一化：包格式允许省略 "Task."/"Service." 前缀（计划里示例就写了 `UpdateNearestPlayer`）。
        /// 命中时返回注册表里的**规范拼写**，保证导出/编辑器写出的是同一种形式。
        /// </summary>
        public static bool TryResolveNodeTypeId(string raw, out string canonical)
        {
            canonical = null;
            BtNodeInfo info;
            if (TryGetNodeInfo(raw, out info))
            {
                canonical = info.TypeId;
                return true;
            }
            if (!string.IsNullOrEmpty(raw) && raw.IndexOf('.') < 0 && TryGetNodeInfo("Task." + raw, out info))
            {
                canonical = info.TypeId;
                return true;
            }
            return false;
        }

        public static bool TryResolveDecoratorTypeId(string raw, out string canonical)
        {
            canonical = null;
            BtDecoratorInfo info;
            if (TryGetDecoratorInfo(raw, out info))
            {
                canonical = info.TypeId;
                return true;
            }
            return false;
        }

        public static bool TryResolveServiceTypeId(string raw, out string canonical)
        {
            canonical = null;
            BtServiceInfo info;
            if (TryGetServiceInfo(raw, out info))
            {
                canonical = info.TypeId;
                return true;
            }
            if (!string.IsNullOrEmpty(raw) && raw.IndexOf('.') < 0
                && TryGetServiceInfo("Service." + raw, out info))
            {
                canonical = info.TypeId;
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- 列表

        public static List<string> ListNodeTypes()
        {
            EnsureInitialized();
            lock (s_gate)
            {
                return new List<string>(s_nodes.Keys);
            }
        }

        public static List<string> ListDecoratorTypes()
        {
            EnsureInitialized();
            lock (s_gate)
            {
                return new List<string>(s_decorators.Keys);
            }
        }

        public static List<string> ListServiceTypes()
        {
            EnsureInitialized();
            lock (s_gate)
            {
                return new List<string>(s_services.Keys);
            }
        }

        public static List<BtNodeInfo> ListNodeInfos()
        {
            EnsureInitialized();
            lock (s_gate)
            {
                var list = new List<BtNodeInfo>(s_nodes.Values);
                list.Sort((a, b) => string.CompareOrdinal(a.TypeId, b.TypeId));
                return list;
            }
        }

        public static List<BtDecoratorInfo> ListDecoratorInfos()
        {
            EnsureInitialized();
            lock (s_gate)
            {
                var list = new List<BtDecoratorInfo>(s_decorators.Values);
                list.Sort((a, b) => string.CompareOrdinal(a.TypeId, b.TypeId));
                return list;
            }
        }

        public static List<BtServiceInfo> ListServiceInfos()
        {
            EnsureInitialized();
            lock (s_gate)
            {
                var list = new List<BtServiceInfo>(s_services.Values);
                list.Sort((a, b) => string.CompareOrdinal(a.TypeId, b.TypeId));
                return list;
            }
        }

        /// <summary>清空自定义注册（测试用；内置类型会重新注册）。</summary>
        public static void ResetToBuiltIns()
        {
            lock (s_gate)
            {
                s_nodes.Clear();
                s_decorators.Clear();
                s_services.Clear();
                s_initialized = false;
            }
            EnsureInitialized();
        }

        // ---------------------------------------------------------------- 内建类型

        private static void RegisterBuiltIns()
        {
            // 组合 / 根
            s_nodes["Root"] = new BtNodeInfo("Root", () => new BtRootNode(),
                BtNodeShape.Root, true, new[]
                {
                    new BtPropertySpec("loop", BtPropertyKind.Bool, false, "true", null,
                        "跑完整棵树之后要不要从头再来（false = 一次性，例如「进入游戏」这种菜单宏）"),
                });
            s_nodes["Selector"] = new BtNodeInfo("Selector", () => new BtSelectorNode(),
                BtNodeShape.Composite, true, new BtPropertySpec[0]);
            s_nodes["Sequence"] = new BtNodeInfo("Sequence", () => new BtSequenceNode(),
                BtNodeShape.Composite, true, new BtPropertySpec[0]);
            s_nodes["SimpleParallel"] = new BtNodeInfo("SimpleParallel", () => new BtSimpleParallelNode(),
                BtNodeShape.Composite, true, new BtPropertySpec[0]);

            // 任务
            s_nodes["Task.Wait"] = new BtNodeInfo("Task.Wait", () => new BtWaitTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Float("seconds", 1f, "等待时长（秒），0 = 本帧成功")
                });

            s_nodes["Task.SetBlackboard"] = new BtNodeInfo("Task.SetBlackboard",
                () => new BtSetBlackboardTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.BlackboardKey("key", "要写入的黑板键名"),
                    BtProps.Enum("valueKind", "float", BtSchema.ValueKinds),
                    BtProps.Any("value", "写入的值（按 valueKind 解释）")
                });

            s_nodes["Task.Log"] = new BtNodeInfo("Task.Log", () => new BtLogTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Str("message", "(log)", "写进日志的内容"),
                    BtProps.Bool("succeed", true, "false 时本节点判失败（用来强制走备用分支）")
                });

            s_nodes["Task.LookAt"] = new BtNodeInfo("Task.LookAt", () => new BtLookAtTargetTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "黑板里存放目标角色的键"),
                    BtProps.Float("eyeHeight", 1.35f, "目标眼睛高度（看向点 = 位置 + 该高度）"),
                    BtProps.Float("seconds", 0.25f, "保持朝向的时长（秒）")
                });

            // 跟随某个角色：先靠近 → 尽量踩他走过的格子 → 走不通就借 A* 绕过去 → 全程保持水平间隔
            s_nodes["Task.FollowEntity"] = new BtNodeInfo("Task.FollowEntity",
                () => new BtFollowEntityTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "跟谁（黑板 actor 键）"),
                    BtProps.Enum("mode", "auto", "auto", "trail", "path"),
                    BtProps.Float("keepDistance", 2f, "保持的水平间隔（米/格）：到了就站住，不往人身上挤"),
                    BtProps.Float("standHysteresis", 0.75f,
                        "站↔走的迟滞（米）：防「距离抖一下就每帧起停」（那会让步伐反复重启）"),
                    BtProps.Int("trailLength", 64, "足迹链最多记多少格"),
                    BtProps.Float("trailMaxAge", 3f, "脚印有效期（秒）：太旧就不用，改走 A*"),
                    BtProps.Float("trailCellRadius", 0.9f, "走到多近算踩上了这一格（米）"),
                    BtProps.Float("trailStuckSeconds", 1.5f, "追脚印时多久没挪窝算卡住（秒）→ 改走 A*"),
                    BtProps.Str("forwardKey", "w", "前进键（Keyboard 名字）"),
                    BtProps.Str("backKey", "s", "后退键（走路方向在身体后方时用；SC 里按 S 速度 6 折）"),
                    BtProps.Str("leftKey", "a", "左平移键（镜头锁着人，脚下横着走）"),
                    BtProps.Str("rightKey", "d", "右平移键"),
                    BtProps.Str("jumpKey", "space", "跳跃键（上台阶用）"),
                    BtProps.Float("jumpHeight", 0.6f, "下一步比脚下高多少才跳（米）"),
                    BtProps.Float("eyeHeight", 1.35f, "看目标/脚印时的眼睛高度"),
                    BtProps.Float("repathSeconds", 0.75f, "A* 重算间隔（秒）"),
                    BtProps.Float("repathMoveThreshold", 1.5f, "目标移动超过这个距离立刻重算（米）"),
                    BtProps.Int("maxPositionsToCheck", 500, "A* 搜索上限"),
                    BtProps.Float("timeout", 0f, "跟随超时（秒）；0 = 一直跟")
                });

            // 看向**黑板里的一个点**（三 float 键）：盯模型节点用（Service.UpdateModelNode → node.X/Y/Z）
            s_nodes["Task.LookAtPoint"] = new BtNodeInfo("Task.LookAtPoint",
                () => new BtLookAtPointTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("xKey", "point.X", "看向点的 X（float 键）"),
                    BtProps.OptionalBlackboardKey("yKey", "point.Y", "看向点的 Y（float 键）"),
                    BtProps.OptionalBlackboardKey("zKey", "point.Z", "看向点的 Z（float 键）"),
                    BtProps.Float("seconds", 0.1f, "保持朝向的时长（秒；期间每个 tick 重新对准）")
                });

            s_nodes["Task.MoveTo"] = new BtNodeInfo("Task.MoveTo", () => new BtMoveToTargetTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "黑板里存放目标角色的键"),
                    BtProps.Float("acceptableRadius", 3f, "进入该半径即算到达（米）"),
                    BtProps.Float("timeout", 25f, "超时判失败（秒）"),
                    BtProps.Str("forwardKey", "w", "前进键（Keyboard 名字）"),
                    BtProps.Float("eyeHeight", 1.35f, "边走边修正朝向时看的目标高度")
                });

            s_nodes["Task.WaitForTarget"] = new BtNodeInfo("Task.WaitForTarget",
                () => new BtWaitForTargetTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "等待出现的黑板键"),
                    BtProps.Float("timeout", 10f, "超时判失败（秒）")
                });

            // 面向目标（与 LookAt 分工：这个的 KPI 是"转到位了"，攻击/交互的前置）
            s_nodes["Task.FaceEntity"] = new BtNodeInfo("Task.FaceEntity",
                () => new BtFaceEntityTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "黑板里存放目标 actor 的键"),
                    BtProps.Float("toleranceDegrees", 12f, "允许的角度误差（度）"),
                    BtProps.Float("timeout", 3f, "超时判失败（秒）"),
                    BtProps.Float("maxTurnPerSecond", 0f, "每秒最多转多少弧度；0 = 用 AI 的瞬时转向")
                });

            // 寻路移动：借游戏自己的 A*（SubsystemPathfinding）算路，再用注入输入跟着走
            s_nodes["Task.NavigateTo"] = new BtNodeInfo("Task.NavigateTo",
                () => new BtNavigateToTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.Enum("source", "actor", "actor", "cell"),
                    BtProps.OptionalBlackboardKey("targetKey", "target", "source=actor 时的目标 actor 键"),
                    BtProps.OptionalBlackboardKey("xKey", "goalX", "source=cell 时的 X（int 键）"),
                    BtProps.OptionalBlackboardKey("yKey", "goalY", "source=cell 时的 Y（int 键）"),
                    BtProps.OptionalBlackboardKey("zKey", "goalZ", "source=cell 时的 Z（int 键）"),
                    BtProps.Float("acceptableRadius", 2.5f, "到这个距离内算到达（米）"),
                    BtProps.Float("timeout", 60f, "超时判失败（秒）"),
                    BtProps.Str("forwardKey", "w", "前进键（Keyboard 名字）"),
                    BtProps.Str("jumpKey", "space", "跳跃键（跳上半格台阶用）"),
                    BtProps.Float("jumpHeight", 0.6f, "航点比脚下高多少才跳（米）"),
                    BtProps.Float("waypointRadius", 0.7f, "判定走到航点的半径（米）"),
                    BtProps.Float("repathSeconds", 2f, "重新请求路径的间隔（秒）"),
                    BtProps.Float("repathMoveThreshold", 1.5f, "目标移动超过这个距离立刻重算（米）"),
                    BtProps.Int("maxPositionsToCheck", 500, "A* 搜索上限（越大越能找到远路、越费 CPU）"),
                    BtProps.Float("eyeHeight", 1.35f, "看航点时的眼睛高度（米）"),
                    BtProps.Float("stuckSeconds", 2f, "卡住判定窗口（秒）"),
                    BtProps.Float("stuckDistance", 0.35f, "卡住的位移阈值（米）")
                });

            // 触发控制面事件（写进 AI 事件日志，编辑器实时监视 / ai.logs 能看到）
            s_nodes["Task.Emit"] = new BtNodeInfo("Task.Emit", () => new BtEmitTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Str("category", "emit", "事件分类（写进日志中括号里）"),
                    BtProps.Str("message", null, "事件内容"),
                    BtProps.OptionalBlackboardKey("key", null, "顺手置位的布尔键（留空只写日志）"),
                    BtProps.Bool("alsoEngineLog", false, "没有事件日志时是否也写游戏日志")
                });

            // ---- 世界交互任务族（BtInteractionTasks.cs）：全部只走"转视角 + 鼠标键 + 数字键/滚轮"
            s_nodes["Task.Mine"] = new BtNodeInfo("Task.Mine", () => new BtMineBlockTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("xKey", "mineX", "目标格子的 X（int 键）"),
                    BtProps.OptionalBlackboardKey("yKey", "mineY", "目标格子的 Y（int 键）"),
                    BtProps.OptionalBlackboardKey("zKey", "mineZ", "目标格子的 Z（int 键）"),
                    BtProps.Str("button", "left", "鼠标键（left/right）"),
                    BtProps.Float("timeout", 20f, "超时判失败（秒）"),
                    BtProps.Bool("requireBlockPresent", true, "读不到目标格时是否直接判失败")
                });

            s_nodes["Task.Attack"] = new BtNodeInfo("Task.Attack", () => new BtAttackTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "要打的 actor（黑板键）"),
                    BtProps.Float("range", 3.5f, "超出这个距离就先靠近（0 = 原地打）"),
                    BtProps.Float("eyeHeight", 1.2f, "看目标哪个高度（米）"),
                    BtProps.Float("timeout", 20f, "超时判失败（秒）"),
                    BtProps.Float("clickInterval", 0.6f, "连点间隔（秒；0 = 一直按住）"),
                    BtProps.Str("button", "left", "鼠标键")
                });

            s_nodes["Task.Interact"] = new BtNodeInfo("Task.Interact", () => new BtInteractTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Enum("source", "cell", "cell", "actor"),
                    BtProps.OptionalBlackboardKey("targetKey", "target", "source=actor 时的目标键"),
                    BtProps.OptionalBlackboardKey("xKey", "interactX", "source=cell 时的 X（int 键）"),
                    BtProps.OptionalBlackboardKey("yKey", "interactY", "source=cell 时的 Y（int 键）"),
                    BtProps.OptionalBlackboardKey("zKey", "interactZ", "source=cell 时的 Z（int 键）"),
                    BtProps.Int("repeat", 1, "点几次（有些交互要双击，例如上马）"),
                    BtProps.Float("interval", 0.35f, "两次点击的间隔（秒）"),
                    BtProps.Str("button", "right", "鼠标键")
                });

            s_nodes["Task.PlaceBlock"] = new BtNodeInfo("Task.PlaceBlock", () => new BtPlaceBlockTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.OptionalBlackboardKey("xKey", "placeX", "目标格子的 X（int 键）"),
                    BtProps.OptionalBlackboardKey("yKey", "placeY", "目标格子的 Y（int 键）"),
                    BtProps.OptionalBlackboardKey("zKey", "placeZ", "目标格子的 Z（int 键）"),
                    BtProps.Int("repeat", 1, "放几次（连放同一格没意义，一般 1）"),
                    BtProps.Float("interval", 0.4f, "两次点击的间隔（秒；游戏的动作冷却是 0.33s，别调更小）"),
                    BtProps.Str("button", "right", "鼠标键")
                });

            s_nodes["Task.SelectSlot"] = new BtNodeInfo("Task.SelectSlot", () => new BtSelectSlotTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Int("slot", 1, "快捷栏槽位（1..9）；0 = 不改槽位、只用滚轮"),
                    BtProps.Int("scroll", 0, "滚轮格数（>0 向前/向左，<0 向后/向右）")
                });

            // 用手上的物品（吃/喝/使用）：没有目标格也要能表达 —— Task.Interact 必须有目标，
            // Task.PlaceBlock 必须有"看得见的面"，而吃东西两者都没有
            s_nodes["Task.UseItem"] = new BtNodeInfo("Task.UseItem", () => new BtUseItemTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Str("button", "right", "鼠标键（SC 用右键使用手上物品）"),
                    BtProps.Float("holdSeconds", 1.2f, "按住多久（秒；吃东西要按住一小会儿）"),
                    BtProps.Int("repeat", 1, "重复几次"),
                    BtProps.Float("interval", 0.35f, "两次之间的间隔（秒）")
                });

            // 跳：NavigateTo 里那一下跳是跟航点自动补的，这一条是"手动跳一次"（跳台阶/跳沟）
            s_nodes["Task.Jump"] = new BtNodeInfo("Task.Jump", () => new BtJumpTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Str("key", "space", "跳跃键（Keyboard 名字）"),
                    BtProps.Int("times", 1, "跳几次"),
                    BtProps.Float("interval", 0.45f, "两次之间的间隔（秒；跳跃有落地判定）"),
                    BtProps.Bool("alsoForward", false, "边跳边按住前进键（跳台阶/跳沟）"),
                    BtProps.Str("forwardKey", "w", "alsoForward 时按住的前进键")
                });

            s_nodes["Task.Subtree"] = new BtNodeInfo("Task.Subtree", () => new BtSubtreeTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.RequiredStr("package", "manifest.references 里的引用 id（可写 \"id#节点id\"）"),
                    BtProps.Str("entry", null, "被引用包内的入口节点 id；为空 = 该包的入口")
                });

            s_nodes["Task.PlayActionPackage"] = new BtNodeInfo("Task.PlayActionPackage",
                () => new BtPlayActionPackageTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.List("packages", "要播放的动作包（相对树包同路径的 .scatpak）"),
                    BtProps.Enum("mode", "Sequence", BtSchema.ActionPackageModes),
                    BtProps.Int("repeat", 1, "重复次数"),
                    BtProps.Bool("abortOnFail", true, "失败是否打断整条分支")
                });

            // 只存在于代码里的委托节点：包格式里出现即报错（校验器据此提示）
            s_nodes["Task.Lambda"] = new BtNodeInfo("Task.Lambda", () => new BtLambdaTask(),
                BtNodeShape.Task, false, new BtPropertySpec[0]);

            // UI 点击（UI-1 服务，2026-09-12）：目标是**语义目标**，坐标由 CmdBridge 在点击那一刻现算
            // —— 改窗口大小/UI 缩放都不会点空（用户明确要求），编辑器里也能"拾取"出这个目标串。
            s_nodes["Task.UiClick"] = new BtNodeInfo("Task.UiClick", () => new BtUiClickTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.RequiredStr("target",
                        "UI 目标：控件名/文本（Play）、路径（[MainMenuScreen#0]/…/Play）、"
                        + "列表行（list:WorldsList@世界名 或 list:WorldsList#0）"),
                    BtProps.Enum("mode", "direct", BtSchema.UiClickModes),
                    BtProps.Float("waitSeconds", 3f, "目标还没出现时最多等多久（秒）"),
                    BtProps.Int("repeat", 1, "连点几次")
                });

            // 装饰器
            s_decorators["Blackboard"] = new BtDecoratorInfo("Blackboard",
                () => new BtBlackboardDecorator(), true, new[]
                {
                    BtProps.BlackboardKey("key", "要检查的黑板键"),
                    BtProps.Enum("query", "IsSet", BtSchema.BlackboardQueries),
                    BtProps.Enum("operator", "==", BtSchema.CompareOperators),
                    BtProps.Enum("valueKind", "bool", BtSchema.ValueKinds),
                    BtProps.Any("value", "Compare 时的比较值")
                });

            s_decorators["Cooldown"] = new BtDecoratorInfo("Cooldown",
                () => new BtCooldownDecorator(), true, new[]
                {
                    BtProps.Float("cooldownSeconds", 5f, "执行一次后的锁定时间（秒）")
                });

            s_decorators["TimeLimit"] = new BtDecoratorInfo("TimeLimit",
                () => new BtTimeLimitDecorator(), true, new[]
                {
                    BtProps.Float("limitSeconds", 5f, "超时判失败（秒）")
                });

            s_decorators["Loop"] = new BtDecoratorInfo("Loop", () => new BtLoopDecorator(), true, new[]
            {
                BtProps.Int("numLoops", 1, "循环次数"),
                BtProps.Bool("infiniteLoop", false, "无限循环"),
                BtProps.Float("infiniteLoopTimeoutSeconds", 10f, "无限循环的超时兜底（秒）")
            });

            s_decorators["ForceSuccess"] = new BtDecoratorInfo("ForceSuccess",
                () => new BtForceSuccessDecorator(), true, new BtPropertySpec[0]);

            s_decorators["ForceFailure"] = new BtDecoratorInfo("ForceFailure",
                () => new BtForceFailureDecorator(), true, new BtPropertySpec[0]);

            // 两个黑板键比较（UE 的 CompareBBEntries）：Blackboard 只能与常量比，
            // "目标比上次更近""两个键是不是同一个目标"这类相对判断需要它。
            s_decorators["CompareBBEntries"] = new BtDecoratorInfo("CompareBBEntries",
                () => new BtCompareBlackboardDecorator(), true, new[]
                {
                    BtProps.BlackboardKey("keyA", "左边那个黑板键（两边都要有值才算条件成立）"),
                    BtProps.BlackboardKey("keyB", "右边那个黑板键"),
                    BtProps.Enum("operator", "==", BtSchema.CompareOperators)
                });

            s_decorators["Inverter"] = new BtDecoratorInfo("Inverter",
                () => new BtInverterDecorator(), true, new BtPropertySpec[0]);

            s_decorators["Condition"] = new BtDecoratorInfo("Condition",
                () => new BtConditionDecorator(), false, new BtPropertySpec[0]);

            // 服务
            s_services["Service.Lambda"] = new BtServiceInfo("Service.Lambda",
                () => new BtLambdaService(), false, new BtPropertySpec[0]);

            s_services["Service.UpdateNearestPlayer"] = new BtServiceInfo(
                "Service.UpdateNearestPlayer", () => new BtUpdateNearestPlayerService(), true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "找到的目标写进哪个黑板键"),
                    BtProps.Str("nameFilter", null, "限定玩家名（大小写不敏感）；为空 = 最近的其它玩家"),
                    BtProps.Bool("clearWhenMissing", false, "找不到时是否清掉黑板键")
                });

            // ---- 传感器服务族（2026-09-12 补）：把"世界里的事实"周期写进黑板，供装饰器判断。
            // 共同约定：找不到就清掉上次的值（否则"狼已经走了"而黑板里还留着旧坐标）。
            s_services["Service.UpdateSelf"] = new BtServiceInfo(
                "Service.UpdateSelf", () => new BtUpdateSelfService(), true, new[]
                {
                    BtProps.Str("prefix", "self.", "键名前缀（默认 self.，写成 self.Food / self.Health …）"),
                    BtProps.Bool("writePosition", true, "是否连坐标与朝向一起写（self.X/Y/Z/Yaw）")
                });

            s_services["Service.UpdateNearestCreature"] = new BtServiceInfo(
                "Service.UpdateNearestCreature", () => new BtUpdateNearestCreatureService(), true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "creature", "找到的生物写进哪个黑板键"),
                    BtProps.Int("categoryMask", 0,
                        "类别掩码：1=陆地掠食者 2=陆地其它 4=水中掠食者 8=水中其它 16=鸟；0=不限"),
                    BtProps.Float("maxDistance", 32f, "搜索半径（米）"),
                    BtProps.Bool("clearWhenMissing", true, "找不到时是否清掉黑板键")
                });

            s_services["Service.UpdateNearestPickable"] = new BtServiceInfo(
                "Service.UpdateNearestPickable", () => new BtUpdateNearestPickableService(), true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "pickable", "找到的掉落物写进哪个黑板键"),
                    BtProps.Float("maxDistance", 24f, "搜索半径（米）"),
                    BtProps.Bool("clearWhenMissing", true, "找不到时是否清掉黑板键")
                });

            s_services["Service.UpdateBlockAhead"] = new BtServiceInfo(
                "Service.UpdateBlockAhead", () => new BtUpdateBlockAheadService(), true, new[]
                {
                    BtProps.OptionalBlackboardKey("key", "mine", "命中了前方方块就写 true 的布尔键（默认与 Task.Mine 的坐标键配对）"),
                    BtProps.Float("maxDistance", 3f, "射线长度（米）"),
                    BtProps.Float("pitchOffset", -0.35f, "俯仰偏移（弧度，负=朝下；默认看脚前那格）"),
                    BtProps.Str("nameKey", "mineName", "命中的方块名写进哪个键（留空则不写）")
                });

            s_services["Service.UpdateLineOfSight"] = new BtServiceInfo(
                "Service.UpdateLineOfSight", () => new BtUpdateLineOfSightService(), true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "要看的目标（actor 键）"),
                    BtProps.OptionalBlackboardKey("key", "canSee", "视线是否通畅写进哪个布尔键"),
                    BtProps.Float("maxDistance", 48f, "超过这个距离就不再射线（直接判不可见）"),
                    BtProps.Float("targetEyeHeight", 1.55f,
                        "射线瞄目标多高（米；默认眼睛高度 —— 只露出头也算看得见）"),
                    BtProps.Bool("alsoCheckBody", true, "是否再加打一条胸口射线（任一通畅即算看得见）"),
                    BtProps.Float("bodyHeight", 0.9f, "胸口射线的高度（米，相对目标脚底）")
                });

            // ---- 通用件（2026-09-13 用户要求："射线检测能不能加""模型节点跟踪"）：
            //      把"每换一个问法就改一次 C#"变成"改属性"，从此少一次部署 + 少一次重启。
            s_services["Service.Probe"] = new BtServiceInfo(
                "Service.Probe", () => new BtProbeService(), true, new[]
                {
                    BtProps.Enum("from", "eye", "eye", "body", "point"),
                    BtProps.Enum("to", "target", "target", "point", "ahead", "down"),
                    BtProps.Enum("mode", "blocked", "blocked", "clear"),
                    BtProps.OptionalBlackboardKey("key", "probe",
                        "主键（另带 Blocked / Reached / Distance / X / Y / Z / Contents / Block）"),
                    BtProps.OptionalBlackboardKey("targetKey", "target", "to=target 时的 actor 键"),
                    BtProps.Float("targetHeight", 1.55f, "to=target 时在目标脚底之上多少米打"),
                    BtProps.Float("pitchOffset", 0f, "to=ahead 时的俯仰偏移（弧度，负=朝下）"),
                    BtProps.OptionalBlackboardKey("fromXKey", "point.X", "from=point 时的起点 X（float 键）"),
                    BtProps.OptionalBlackboardKey("fromYKey", "point.Y", "from=point 时的起点 Y（float 键）"),
                    BtProps.OptionalBlackboardKey("fromZKey", "point.Z", "from=point 时的起点 Z（float 键）"),
                    BtProps.OptionalBlackboardKey("toXKey", "point.X", "to=point 时的终点 X（float 键）"),
                    BtProps.OptionalBlackboardKey("toYKey", "point.Y", "to=point 时的终点 Y（float 键）"),
                    BtProps.OptionalBlackboardKey("toZKey", "point.Z", "to=point 时的终点 Z（float 键）"),
                    BtProps.Float("maxDistance", 16f, "射线长度（米）")
                });

            s_services["Service.UpdateModelNode"] = new BtServiceInfo(
                "Service.UpdateModelNode", () => new BtUpdateModelNodeService(), true, new[]
                {
                    BtProps.OptionalBlackboardKey("targetKey", "target", "看哪个 actor（黑板 actor 键）"),
                    BtProps.Str("nodeName", "Head",
                        "模型节点名：人形 Body/Head/Hand1/Hand2/Leg1/Leg2；四足另有 Neck/Leg3/Leg4 等"),
                    BtProps.Str("prefix", "node.", "写出去的键前缀（node.X/Y/Z、node.Exists、node.Distance）"),
                    BtProps.Bool("clearWhenMissing", true, "取不到节点时是否清掉坐标键")
                });
        }
    }

    /// <summary>委托服务：把周期性工作写成 lambda（原型与测试用，不参与包格式）。</summary>
    public sealed class BtLambdaService : BtService
    {
        public Action<BtContext> TickDelegate { get; set; }

        public override string NodeType
        {
            get { return "Service.Lambda"; }
        }

        protected override void OnTick(BtContext context)
        {
            if (TickDelegate != null)
                TickDelegate(context);
        }
    }

    /// <summary>
    /// 周期性把"最近的其它玩家"写进黑板（含按名字过滤）。
    /// 这是示例树与后续跟随/接近类行为的公共传感器服务。
    /// </summary>
    public sealed class BtUpdateNearestPlayerService : BtService
    {
        /// <summary>写入的目标键（类型是 <see cref="AiActorView"/>）。</summary>
        public string TargetKey { get; set; } = "target";

        /// <summary>可选：只认这个名字的玩家（大小写不敏感）。空 = 最近的其它玩家。</summary>
        public string NameFilter { get; set; }

        /// <summary>找不到目标时是否清掉黑板键（默认保留上一次的值）。</summary>
        public bool ClearWhenMissing { get; set; }

        public override string NodeType
        {
            get { return "Service.UpdateNearestPlayer"; }
        }

        protected override void OnTick(BtContext context)
        {
            IAiSensor sensors = context.Sensors;
            AiBlackboard blackboard = context.Blackboard;
            if (sensors == null || blackboard == null || !sensors.IsReady)
                return;

            AiActorView view;
            bool found = !string.IsNullOrEmpty(NameFilter)
                ? sensors.TryFindPlayer(NameFilter, out view)
                : sensors.TryFindNearestPlayer(out view);

            if (found)
            {
                blackboard.Set(new AiBlackboardKey<AiActorView>(TargetKey), view);
                blackboard.Set(new AiBlackboardKey<float>(TargetKey + "Distance"), view.Distance);
            }
            else if (ClearWhenMissing)
            {
                blackboard.Remove(new AiBlackboardKey<AiActorView>(TargetKey));
            }
        }
    }
}

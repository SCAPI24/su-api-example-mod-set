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
                    BtProps.RequiredStr("key", "要写入的黑板键名"),
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
                    BtProps.Str("targetKey", "target", "黑板里存放目标角色的键"),
                    BtProps.Float("eyeHeight", 1.35f, "目标眼睛高度（看向点 = 位置 + 该高度）"),
                    BtProps.Float("seconds", 0.25f, "保持朝向的时长（秒）")
                });

            s_nodes["Task.MoveTo"] = new BtNodeInfo("Task.MoveTo", () => new BtMoveToTargetTask(),
                BtNodeShape.Task, true, new[]
                {
                    BtProps.Str("targetKey", "target", "黑板里存放目标角色的键"),
                    BtProps.Float("acceptableRadius", 3f, "进入该半径即算到达（米）"),
                    BtProps.Float("timeout", 25f, "超时判失败（秒）"),
                    BtProps.Str("forwardKey", "w", "前进键（Keyboard 名字）"),
                    BtProps.Float("eyeHeight", 1.35f, "边走边修正朝向时看的目标高度")
                });

            s_nodes["Task.WaitForTarget"] = new BtNodeInfo("Task.WaitForTarget",
                () => new BtWaitForTargetTask(), BtNodeShape.Task, true, new[]
                {
                    BtProps.Str("targetKey", "target", "等待出现的黑板键"),
                    BtProps.Float("timeout", 10f, "超时判失败（秒）")
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
                    BtProps.RequiredStr("key", "要检查的黑板键"),
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
                    BtProps.Str("targetKey", "target", "找到的目标写进哪个黑板键"),
                    BtProps.Str("nameFilter", null, "限定玩家名（大小写不敏感）；为空 = 最近的其它玩家"),
                    BtProps.Bool("clearWhenMissing", false, "找不到时是否清掉黑板键")
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

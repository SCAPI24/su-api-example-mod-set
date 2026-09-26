using System;
using System.Collections.Generic;
using System.IO;

namespace PlayerAiMod
{
    /// <summary>
    /// 游戏侧的动作技能装配（P1）：把"当前宿主"的能力接给动作层。
    ///
    /// 关键点：**每次访问都现解析宿主**（<c>PlayerAiRuntime.ResolveTreeHost()</c>），
    /// 于是"世界内 → 世界外"切换时不需要重装服务 —— 这正是既有的
    /// "行为树不绑定角色、绑定控制器"设计（见 <see cref="IPlayerInputProvider"/> 的说明）。
    ///
    /// 能力不足时**如实降级**（不假装成功）：
    ///   · 没有宿主/没有执行器 → <see cref="IActionExecutor.IsReady"/> 为 false → 动作层开局判失败；
    ///   · `<c>aim</c>` 语义目标需要"准星命中的格子"，当前 <see cref="IAiSensor"/> 还没有这项能力，
    ///     所以会返回明确错误（`target_unavailable: aim`），而不是随便挑一个格子。
    ///     补齐它属于 P2（摘要/观察增强）的范围。
    /// </summary>
    public sealed class GameActionServices : IActionSkillProvider
    {
        /// <summary>`aim` 还没接通时给出的错误码（调用方/日志据此区分"没有目标"与"注不进去"）。</summary>
        public const string TargetUnavailable = "target_unavailable";

        /// <summary>
        /// 把 CmdBridge 的 `DescribeAim()` 观察结果转成动作层目标（**纯函数，便于自检**）。
        /// 任何"看不出来"的情形都返回 null + 明确原因，绝不猜一个格子。
        /// </summary>
        public static ActionTarget TargetFromAimObservation(Dictionary<string, object> aim, out string error)
        {
            error = null;
            if (aim == null)
            {
                error = TargetUnavailable + ": aim observation returned nothing";
                return null;
            }
            if (!Truthy(aim, "loaded") || !Truthy(aim, "active"))
            {
                error = TargetUnavailable + ": no world/player or camera to aim with";
                return null;
            }

            Dictionary<string, object> target = GetDictionary(aim, "target");
            if (target == null)
            {
                error = TargetUnavailable + ": crosshair is not pointing at anything";
                return null;
            }

            string kind = GetString(target, "kind");
            if (!string.Equals(kind, "block", StringComparison.OrdinalIgnoreCase))
            {
                error = TargetUnavailable + ": crosshair target is not a block (kind=" + (kind ?? "?") + ")";
                return null;
            }

            Dictionary<string, object> cell = GetDictionary(target, "cell");
            if (cell == null)
            {
                error = TargetUnavailable + ": aim target has no cell";
                return null;
            }

            return ActionTarget.Cell(GetInt(cell, "x"), GetInt(cell, "y"), GetInt(cell, "z"));
        }

        /// <summary>
        /// 把 CmdBridge 的 `DescribePlayer()` 观察结果转成"第一个可食用物品的槽位"
        /// （用 <see cref="ActionTarget.X"/> 回传 1..10 的槽位号，与 eat verb 的约定一致）。
        ///
        /// 判据是 `Block.GetNutritionalValue(value) &gt; 0` —— 不靠名字猜。
        /// 这是**确定性规则**：同一背包同一时刻必然挑同一个槽位（可复现、可复盘）。
        /// **纯函数，便于自检。**
        /// </summary>
        public static ActionTarget TargetFirstFoodFromPlayerObservation(Dictionary<string, object> player,
            out string error)
        {
            error = null;
            if (player == null || !Truthy(player, "loaded"))
            {
                error = TargetUnavailable + ": no player to read the inventory from";
                return null;
            }

            Dictionary<string, object> inventory = GetDictionary(player, "inventory");
            if (inventory == null)
            {
                error = TargetUnavailable + ": player has no inventory";
                return null;
            }

            List<object> slots = GetList(inventory, "slots");
            if (slots == null || slots.Count == 0)
            {
                error = TargetUnavailable + ": inventory is empty";
                return null;
            }

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i] as Dictionary<string, object>;
                if (slot == null)
                    continue;

                int value = GetInt(slot, "value");
                int count = GetInt(slot, "count");
                if (value == 0 || count <= 0)
                    continue;

                int contents = GetInt(slot, "contents");
                bool edible;
                try
                {
                    Game.Block block = Game.BlocksManager.Blocks[contents];
                    edible = block != null && block.GetNutritionalValue(value) > 0f;
                }
                catch (Exception)
                {
                    edible = false;
                }
                if (!edible)
                    continue;

                int slotIndex = GetInt(slot, "slot");
                return new ActionTarget("cell")
                {
                    // 槽位号用 1..10（够 hotbar/eat 直接用），索引在此 +1
                    X = slotIndex + 1,
                    Y = 0,
                    Z = 0
                };
            }

            error = TargetUnavailable + ": no edible item in the inventory";
            return null;
        }

        // ---- 只读观察返回值的小工具（字典/列表，来自 CmdBridge 的 JSON 化输出）----

        internal static bool Truthy(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                return false;
            var asBool = raw as bool?;
            if (asBool.HasValue)
                return asBool.Value;
            string text = raw as string;
            if (text != null)
                return !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        internal static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw))
                return null;
            return raw as Dictionary<string, object>;
        }

        internal static List<object> GetList(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw))
                return null;
            return raw as List<object>;
        }

        internal static string GetString(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                return null;
            return raw as string;
        }

        internal static int GetInt(Dictionary<string, object> source, string key)
        {
            object raw;
            if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                return 0;
            if (raw is int)
                return (int)raw;
            if (raw is long)
                return (int)(long)raw;
            if (raw is float)
                return (int)(float)raw;
            if (raw is double)
                return (int)(double)raw;
            if (raw is string)
            {
                int parsed;
                return int.TryParse((string)raw, out parsed) ? parsed : 0;
            }
            return 0;
        }

        private readonly PlayerAiRuntime m_runtime;
        private readonly ActionScriptLibrary m_library;
        private readonly LazyExecutor m_executor;

        public IActionTargetResolver Targets { get; }

        public IActionGuardEvaluator Guards { get; }

        public GameActionServices(PlayerAiRuntime runtime, IEnumerable<string> scriptDirectories)
        {
            m_runtime = runtime;
            m_library = new ActionScriptLibrary(scriptDirectories);
            m_executor = new LazyExecutor(this);
            Targets = new HostTargetResolver(this);
            Guards = new HostGuardEvaluator(this);
        }

        public ActionScriptLibrary Library
        {
            get { return m_library; }
        }

        public IActionExecutor Executor
        {
            get { return m_executor; }
        }

        public IReadOnlyList<string> ScriptDirectories
        {
            get { return m_library.Directories; }
        }

        public ActionScript ResolveScript(string nameOrPath, out string error)
        {
            return m_library.Resolve(nameOrPath, out error);
        }

        /// <summary>当前宿主（世界内是角色宿主；世界外是 UI 宿主）。</summary>
        internal IAiTreeHost Host
        {
            get { return m_runtime != null ? m_runtime.ResolveTreeHost() : null; }
        }

        public Dictionary<string, object> Describe()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            IAiTreeHost host = Host;
            result["host"] = host != null ? host.HostName : "<none>";
            result["hostKind"] = host != null ? host.HostKind : "<none>";
            result["hostReady"] = host != null && host.IsReady;
            result["executorReady"] = m_executor.IsReady;
            result["sensorReady"] = host != null && host.Sensors != null && host.Sensors.IsReady;
            result["scriptDirectories"] = new List<string>(m_library.Directories);
            result["scriptsCached"] = m_library.CacheCount;
            result["scriptParseFailures"] = m_library.ParseFailures;
            result["verbs"] = new List<string>(ScriptCompiler.VerbNames);
            return result;
        }

        // ---------------------------------------------------------------- 执行器：每次现取宿主

        /// <summary>
        /// 把当前宿主的 <see cref="IAiActuator"/> 包成动作层能用的执行器。
        /// 为什么不做成"绑定一次"：宿主会在世界进出时切换，绑定一次就会指向已经失效的对象。
        /// </summary>
        private sealed class LazyExecutor : IActionExecutor
        {
            private readonly GameActionServices m_owner;
            private GameActionExecutor m_current;

            public LazyExecutor(GameActionServices owner)
            {
                m_owner = owner;
            }

            private IActionExecutor Resolve()
            {
                IAiTreeHost host = m_owner.Host;
                IAiActuator actuator = host != null ? host.Actuators : null;
                if (actuator == null)
                {
                    m_current = null;
                    return null;
                }
                if (m_current == null || !ReferenceEquals(m_current.Actuator, actuator))
                    m_current = new GameActionExecutor(actuator);
                return m_current;
            }

            public bool IsReady
            {
                get
                {
                    IActionExecutor executor = Resolve();
                    return executor != null && executor.IsReady;
                }
            }

            public bool HoldKeys(IReadOnlyList<string> keys, bool down)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.HoldKeys(keys, down);
            }

            public bool PulseKey(string key)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.PulseKey(key);
            }

            public bool MouseAction(string button, string action)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.MouseAction(button, action);
            }

            public bool Wheel(int notches)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.Wheel(notches);
            }

            public bool SelectSlot(int slot)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.SelectSlot(slot);
            }

            public bool Look(float yawDegrees, float pitchDegrees)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.Look(yawDegrees, pitchDegrees);
            }

            public bool LookDelta(float yawDeltaDegrees, float pitchDeltaDegrees)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.LookDelta(yawDeltaDegrees, pitchDeltaDegrees);
            }

            public bool LookAt(float x, float y, float z)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.LookAt(x, y, z);
            }

            public bool UiClick(string selector)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.UiClick(selector);
            }

            public bool TypeText(string text)
            {
                IActionExecutor executor = Resolve();
                return executor != null && executor.TypeText(text);
            }

            public void ReleaseAll()
            {
                IActionExecutor executor = Resolve();
                if (executor != null)
                    executor.ReleaseAll();
            }
        }

        // ---------------------------------------------------------------- 语义目标（§4.9）

        /// <summary>
        /// 语义目标解析（§4.9：Laya 给枚举、C# 给具体值）。**已接通**：
        /// `self` / 显式坐标 / `entity`（最近玩家）/ **`aim`（准星命中的方块格）** /
        /// **`first_food`（背包里第一个可食用物品的槽位，用 X 回传）**。
        ///
        /// 全部现算（执行那一刻），所以模型永远不见坐标；算不出来就**明确失败**，绝不猜一个格子。
        /// </summary>
        private sealed class HostTargetResolver : IActionTargetResolver
        {
            private readonly GameActionServices m_owner;

            public HostTargetResolver(GameActionServices owner)
            {
                m_owner = owner;
            }

            public ActionTarget Resolve(string target, out string error)
            {
                error = null;
                if (string.IsNullOrEmpty(target))
                    return null;

                string name = target.Trim().ToLowerInvariant();

                IAiTreeHost host = m_owner.Host;
                IAiSensor sensors = host != null ? host.Sensors : null;

                if (name == "self")
                {
                    if (sensors == null || !sensors.IsReady)
                    {
                        error = TargetUnavailable + ": no ready host to resolve 'self'";
                        return null;
                    }
                    Engine.Vector3 position = sensors.Position;
                    return ActionTarget.Point(position.X, position.Y + 1.35f, position.Z);
                }

                if (name == "aim")
                {
                    return ResolveAimTarget(out error);
                }

                if (name == "first_food")
                {
                    return ResolveFirstFood(out error);
                }

                if (name == "entity")
                {
                    if (sensors == null || !sensors.IsReady)
                    {
                        error = TargetUnavailable + ": no ready host to resolve 'entity'";
                        return null;
                    }
                    AiActorView view;
                    if (!sensors.TryFindNearestPlayer(out view))
                    {
                        error = TargetUnavailable + ": no other player found for 'entity'";
                        return null;
                    }
                    return ActionTarget.Entity(view.Name, view.Position.X, view.Position.Y + 1.35f, view.Position.Z);
                }

                error = TargetUnavailable + ": unknown semantic target '" + target + "'";
                return null;
            }

            /// <summary>
            /// 准星命中的方块格：走 CmdBridge 的观察门面（`AimObserver`），
            /// 不在这里自己反射 `SubsystemTerrain` —— 观察口径只允许有一份实现。
            /// </summary>
            private static ActionTarget ResolveAimTarget(out string error)
            {
                error = null;
                CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
                if (facade == null)
                {
                    error = TargetUnavailable + ": CmdBridgeMod facade is not available";
                    return null;
                }
                return TargetFromAimObservation(facade.DescribeAim(8f), out error);
            }

            /// <summary>
            /// 背包里第一个可食用物品的**槽位**（用 <see cref="ActionTarget.X"/> 回传，与 eat verb 的约定一致）。
            /// 判据用 `Block.GetNutritionalValue`（0 = 不能吃），不吃"按名字猜"那一套。
            /// </summary>
            private static ActionTarget ResolveFirstFood(out string error)
            {
                CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
                if (facade == null)
                {
                    error = TargetUnavailable + ": CmdBridgeMod facade is not available";
                    return null;
                }
                return TargetFirstFoodFromPlayerObservation(facade.DescribePlayer(), out error);
            }
            // ---- 只读观察返回值的小工具（都是字典/列表，来自 CmdBridge 的 JSON 化输出）----

            private static bool Truthy(Dictionary<string, object> source, string key)
            {
                object raw;
                if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                    return false;
                var asBool = raw as bool?;
                if (asBool.HasValue)
                    return asBool.Value;
                string text = raw as string;
                if (text != null)
                    return !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase);
                return true;
            }

            private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
            {
                object raw;
                if (source == null || !source.TryGetValue(key, out raw))
                    return null;
                return raw as Dictionary<string, object>;
            }

            private static List<object> GetList(Dictionary<string, object> source, string key)
            {
                object raw;
                if (source == null || !source.TryGetValue(key, out raw))
                    return null;
                return raw as List<object>;
            }

            private static string GetString(Dictionary<string, object> source, string key)
            {
                object raw;
                if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                    return null;
                return raw as string;
            }

            private static int GetInt(Dictionary<string, object> source, string key)
            {
                object raw;
                if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                    return 0;
                if (raw is int)
                    return (int)raw;
                if (raw is long)
                    return (int)(long)raw;
                if (raw is float)
                    return (int)(float)raw;
                if (raw is double)
                    return (int)(double)raw;
                return 0;
            }
        }

        /// <summary>解析器要拿宿主，但接口方法没有上下文 —— 构造时把所属服务注入进来。</summary>
        private static GameActionServices s_owner;

        // ---------------------------------------------------------------- 守卫

        /// <summary>
        /// 守卫判定：词汇与 `obs.waitFor` 一致（<see cref="ActionGuards"/>），
        /// 能判的用传感器直接判，判不了的**明确返回 false + 原因**（不当成"通过"）。
        /// </summary>
        private sealed class HostGuardEvaluator : IActionGuardEvaluator
        {
            private readonly GameActionServices m_owner;

            public HostGuardEvaluator(GameActionServices owner)
            {
                m_owner = owner;
            }

            public bool IsReady
            {
                get { return true; }
            }

            public bool Evaluate(string guard, out string error)
            {
                error = null;
                if (string.IsNullOrEmpty(guard))
                    return true;

                // 判定规则在 `ActionGuardRules`（纯逻辑，自检里逐条钉过）；这里只负责"把事实取来"：
                //   · 观察状态一次问完（走 CmdBridge 的只读门面，口径与 obs.waitFor 一致）；
                //   · 元素事实按需问（`element.*` 守卫才问，省掉绝大多数守卫的一次 UI 解析）。
                CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
                Dictionary<string, object> context = facade != null ? facade.DescribeActionContext() : null;

                bool result;
                if (ActionGuardRules.TryEvaluate(guard.Trim(), context, ProbeElement, out result, out error))
                    return result;

                error = TargetUnavailable + ": guard '" + guard.Trim() + "' is not wired yet"
                    + " (wired: " + ActionGuardRules.DescribeWired() + ")";
                return false;
            }

            /// <summary>
            /// 问一次"这个语义目标现在在不在、能不能点"。
            ///
            /// 为什么不能只判 `!= null`：游戏侧返回的字典同时承载"不存在"与"查询失败"两件事 ——
            /// 前者是正常答案（`absent=true`），后者必须让守卫**判不了**（`Known=false`）。
            /// 混淆这两者会让"UI 还没就绪"被当成"元素不存在"，或者反过来把失败当成通过。
            /// </summary>
            private static UiElementFacts ProbeElement(string selector)
            {
                CmdBridgeMod.CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
                if (facade == null)
                    return UiElementFacts.Unknown("CmdBridgeMod facade is not available");

                Dictionary<string, object> raw = facade.QueryUiElement(selector);
                if (raw == null)
                    return UiElementFacts.Unknown("the UI query returned nothing");

                string queryError = raw.ContainsKey("error") ? raw["error"] as string : null;
                if (!string.IsNullOrEmpty(queryError))
                    return UiElementFacts.Unknown(raw.ContainsKey("reason") ? raw["reason"] as string : queryError);

                string reason = raw.ContainsKey("reason") ? raw["reason"] as string : null;
                // 先看"明确的不存在"标记，再看缺省情况：`QueryUiElement` 成功时会写 `present=true`
                // （老版本没写这个字段，所以这里也接受"有 present 且为 true"这一种）。
                bool absent = raw.ContainsKey("absent") && raw["absent"] is bool && (bool)raw["absent"];
                bool present = raw.ContainsKey("present") && raw["present"] is bool && (bool)raw["present"];
                if (absent || !present)
                    return UiElementFacts.Absent(reason);

                bool hittable = raw.ContainsKey("hittable") && raw["hittable"] is bool && (bool)raw["hittable"];
                bool clickable = raw.ContainsKey("clickable") && raw["clickable"] is bool && (bool)raw["clickable"];
                return UiElementFacts.Found(hittable, clickable, reason);
            }

            private static bool AsBool(Dictionary<string, object> source, string key)
            {
                object raw;
                if (source == null || !source.TryGetValue(key, out raw) || raw == null)
                    return false;
                if (raw is bool)
                    return (bool)raw;
                string asText = raw as string;
                if (asText != null)
                    return !string.Equals(asText, "false", StringComparison.OrdinalIgnoreCase);
                return true;
            }
        }

        /// <summary>把服务登记为"当前"（运行时持有它，同时给静态一份，兼容其它读取点）。</summary>
        public static void Publish(GameActionServices services)
        {
            if (services == null)
                return;
            s_owner = services;
            ActionPlayServices.Current = services;
        }

        /// <summary>脚本目录：实例根下的 `PlayerAi/Scripts` 与 `PlayerAi/BehaviorTrees`（与包目录同源）。</summary>
        public static List<string> DirectoriesFor(string instanceRoot)
        {
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(instanceRoot))
            {
                directories.Add(Path.Combine(instanceRoot, "PlayerAi", "Scripts"));
                directories.Add(Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees"));
            }
            return directories;
        }
        /// <summary>装配到全局（<see cref="PlayerAiRuntime"/> 启动时调用）。</summary>
        public static GameActionServices Install(PlayerAiRuntime runtime, string instanceRoot)
        {
            var directories = new List<string>();
            if (!string.IsNullOrEmpty(instanceRoot))
            {
                directories.Add(Path.Combine(instanceRoot, "PlayerAi", "Scripts"));
                directories.Add(Path.Combine(instanceRoot, "PlayerAi", "BehaviorTrees"));
            }

            var services = new GameActionServices(runtime, directories);
            s_owner = services;
            ActionPlayServices.Current = services;
            return services;
        }

        /// <summary>卸载（Mod 卸载时调用，避免指向已经失效的运行时）。</summary>
        public static void Uninstall()
        {
            s_owner = null;
            ActionPlayServices.Current = null;
        }

        /// <summary>
        /// 取当前服务；**拿不到就按运行时重建一次**。
        ///
        /// 为什么要自愈（实机踩过）：命令经 CmdBridge 通道异步执行，而全局装配点是 Mod 加载时
        /// 赋值的一次性静态字段 —— 实机出现过"同一份 DLL，重启后有时所有 `ai.action.script.*`
        /// 都报 `not_ready`"（静态被清掉、运行时却还在）。
        /// 与其假设"装配一定发生在命令之前"，不如让每次取用都能恢复：
        /// 运行时还在 → 用它的包目录重算脚本目录并重建服务；运行时没了 → 才如实报错。
        /// </summary>
        public static GameActionServices Ensure(PlayerAiRuntime runtime = null)
        {
            GameActionServices current = s_owner;
            if (current != null)
                return current;

            IActionSkillProvider existing = ActionPlayServices.Current;
            var asServices = existing as GameActionServices;
            if (asServices != null)
            {
                s_owner = asServices;
                return s_owner;
            }

            PlayerAiRuntime effective = runtime ?? PlayerAiRuntime.Instance;
            if (effective == null)
                return null;

            // 运行时是唯一权威：它自己会懒创建并 Publish（含脚本目录）
            return effective.ActionServices;
        }

        /// <summary>从包目录反推实例根（`<实例根>/PlayerAi/BehaviorTrees` → `<实例根>`）。</summary>
        public static string ResolveInstanceRoot(PlayerAiRuntime runtime)
        {
            if (runtime == null || runtime.Roots == null || runtime.Roots.InstanceRoot == null)
                return null;

            string packageDirectory = runtime.Roots.InstanceRoot.Path;
            if (string.IsNullOrEmpty(packageDirectory))
                return null;

            try
            {
                string playerAiDirectory = Path.GetDirectoryName(packageDirectory);
                if (string.IsNullOrEmpty(playerAiDirectory))
                    return null;
                string root = Path.GetDirectoryName(playerAiDirectory);
                return !string.IsNullOrEmpty(root) ? root : playerAiDirectory;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

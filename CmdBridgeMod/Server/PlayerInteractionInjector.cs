using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 玩家"交互输入"注入：`act.use` / `act.interact` / `act.place` / `act.aim`。
    ///
    /// ==================================================================================
    /// 引擎事实（全部来自源码，文中行号即证据）
    ///
    /// 1) "使用 / 交互 / 放置"在引擎里**只有一个输入**：鼠标右键。
    ///      ComponentInput.cs:185  PlayerInput.Aim      = IsMouseButtonDown(Right)      ? Ray3(viewPosition, viewDirection)
    ///      ComponentInput.cs:186  PlayerInput.Interact = IsMouseButtonDownOnce(Right)  ? Ray3(...)
    ///    `PlayerInput` 里没有独立的"使用/放置"字段。`ComponentPlayer.Update` 拿到
    ///    `PlayerInput.Interact` 后按**固定顺序**尝试三步（ComponentPlayer.cs:151-181）：
    ///      ① ComponentMiner.Use(ray)      —— 手持方块的 `OnUse`（水桶/船/口哨/火柴/锤/耙/鞍/肥料）
    ///      ② ComponentMiner.Interact(ray) —— 命中方块的 `OnInteract`（箱子/工作台/门/开关/按钮/告示牌…）
    ///      ③ ComponentMiner.Place(ray)    —— 放置手持方块
    ///    所以 `act.use` / `act.interact` / `act.place` 注入的是**同一个输入**，
    ///    区别只在回包报告的"预期分支"与调用方语义；引擎顺序无法用输入改变（真人右键也一样）。
    ///    若手持物有 `OnUse` 行为，引擎**总是**先走 Use —— `act.place` 也不例外，回包会用
    ///    `warnings` 把这件事说清楚，不会让人以为"放了却变成用了"。
    ///
    ///    补充：**吃食物 / 喝牛奶不是这条通道**。营养值是在背包界面里"拖进衣物格"处理的
    ///    （ComponentClothing.cs:410-443 → ComponentVitalStats.Eat，ProcessSlotItems），
    ///    引擎没有"吃"的输入字段。要吃东西请用 `ui` / `click` 驱动背包界面。
    ///
    /// 2) "瞄准"是**按住**语义：`PlayerInput.Aim` 在右键按住期间每帧都有值，
    ///    bow / crossbow / musket / spear 的 `OnAim(InProgress)` 靠它续力
    ///    （SubsystemBowBlockBehavior.cs:68、SubsystemCrossbowBlockBehavior.cs:66、
    ///    SubsystemMusketBlockBehavior.cs:65、SubsystemThrowableBlockBehavior.cs:20）。
    ///    松开右键 → 下一帧没有 Aim → `ComponentPlayer` 走 `AimState.Completed`
    ///    （ComponentPlayer.cs:219-227）→ 射出去 / 扔出去。
    ///
    /// 3) "取消瞄准"在鼠标/键盘通道**没有对应输入**：Aim 射线永远来自相机视线
    ///    （`WorldToScreen(pos + dir)` 恒在屏幕中心），永远进不了引擎的
    ///    "射线出屏 → Cancelled" 分支（ComponentPlayer.cs:190, 211-216），该分支只有触摸通道会撞上
    ///    （ComponentInput.cs:473, 478 用偏移后的方向）。所以 `act.aim cancel` 直接做引擎自己
    ///    取消时做的那两件事：`ComponentMiner.Aim(ray, AimState.Cancelled)` + `m_aim = null`
    ///    （ComponentPlayer.cs:211-214），然后松开右键。这是本文件**唯一**一处非输入写入，
    ///    不清 `m_aim` 的话松手必然触发 Completed = 把箭射出去（ComponentPlayer.cs:221-227）。
    ///
    /// ==================================================================================
    /// 注入机制与 `act.mouse` / `act.key` 完全同源（硬规则：只用虚拟输入，绝不抢真实鼠标/窗口）：
    ///   · 帧首写引擎自己的 `Mouse.m_mouseButtonsDownArray` / `m_mouseButtonsDownOnceArray`
    ///     （`InputWhitelist` 白名单），不经过 OS、不调用 `SetCursorPos`/`SendInput`、不激活窗口；
    ///   · 失焦/共控由 `FocusPolicy` 负责：`focus.detach` 或 `auto` 且真实焦点不在游戏时，
    ///     它会在帧首把 `Window.m_state` 置成 Active，引擎才肯读这份输入；
    ///   · 引擎自己会在失焦时 `Mouse.Clear()`，这里每一步都如实回报"引擎是否真的看到了按下"，
    ///     绝不把"命令返回 200"当成"动作生效"。
    /// </summary>
    internal sealed class PlayerInteractionInjector
    {
        /// <summary>
        /// 右键 = 引擎的"使用/交互/放置/瞄准"键（ComponentInput.cs:185-186）。
        /// 左键是 Hit/Dig（攻击/挖掘），已由 `act.mouse` / `act.dig` 覆盖，这里不碰。
        /// </summary>
        private const MouseButton ActionButton = MouseButton.Right;

        /// <summary>单次 hold 上限：客户端默认请求超时 20s，留足余量。</summary>
        private const int MaxHoldMs = 10000;

        /// <summary>
        /// 松手后等引擎把 `AimState.Completed` 跑完再数一次投射物用的时间（毫秒）。
        /// 两帧量级足够：Completed 就在松开后的下一帧 `ComponentPlayer.Update` 里执行。
        /// </summary>
        private const int SettleMs = 120;

        private readonly InputInjector m_injector;

        /// <summary>串行化"按下 / 松开"的状态机（ControlServer 每个客户端一个线程，可能并发）。</summary>
        private readonly object m_sync = new object();

        /// <summary>`act.aim start` 按住期间为 true；`release` / `cancel` / `act.releaseall` 清掉。</summary>
        private bool m_aimActive;

        internal PlayerInteractionInjector(InputInjector injector)
        {
            if (injector == null)
                throw new ArgumentNullException(nameof(injector));

            m_injector = injector;
        }

        /// <summary>当前是否有 `act.aim start` 按住的瞄准（只读，供 `act.releaseall` / 排查用）。</summary>
        internal bool AimHeld
        {
            get
            {
                lock (m_sync)
                    return m_aimActive;
            }
        }

        /// <summary>
        /// `act.releaseall` 走这里：注入器会把鼠标数组整体清零（`InputInjector.ReleaseAll`），
        /// 本类自己的瞄准簿记必须同时失效，否则下一次 `act.aim start` 会误报 `already_aiming`。
        /// </summary>
        internal void ResetAimSession()
        {
            lock (m_sync)
                m_aimActive = false;
        }

        // ---------------------------------------------------------------- 使用 / 交互 / 放置

        /// <summary>`act.use`：右键边沿 → `ComponentMiner.Use`（手持物 OnUse）。</summary>
        internal object Use(int holdMilliseconds)
        {
            return Pulse("use", holdMilliseconds);
        }

        /// <summary>`act.interact`：右键边沿 → `ComponentMiner.Interact`（方块 OnInteract）。</summary>
        internal object Interact(int holdMilliseconds)
        {
            return Pulse("interact", holdMilliseconds);
        }

        /// <summary>`act.place`：右键边沿 → `ComponentMiner.Place`（放置手持方块）。</summary>
        internal object Place(int holdMilliseconds)
        {
            return Pulse("place", holdMilliseconds);
        }

        private object Pulse(string actionName, int holdMilliseconds)
        {
            m_injector.EnsureEnabled();
            int hold = NormalizeHold(holdMilliseconds);

            // 引擎里右键只有一个：`act.aim start` 正按着的时候再发一次边沿，会先把瞄准松开
            // （松开就会 Completed = 射出去），而调用方以为只是"用一下手持物"。
            // 与其静默地把别人的瞄准射出去，不如明确拒绝并告诉他先收尾。
            lock (m_sync)
            {
                if (m_aimActive)
                {
                    throw new BridgeCommandException("aim_in_progress",
                        "act." + actionName + " needs the same right button that act.aim start is "
                        + "currently holding; sending it would release the aim and fire. Finish the aim "
                        + "first (act.aim release / act.aim cancel), or call act.releaseall.");
                }
            }

            var watch = new Stopwatch();
            bool pressed = false;
            try
            {
                Dictionary<string, object> before =
                    (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                    {
                        ComponentPlayer player = RequirePlayerReady();
                        Dictionary<string, object> snapshot = DescribeAction(player, actionName, hold);
                        // 帧首按下：本帧帧体里的 ComponentInput 才读得到 downOnce。
                        m_injector.SetMouseHeld(ActionButton, true);
                        pressed = true;
                        watch.Start();
                        return snapshot;
                    });

                if (hold > 0)
                    Thread.Sleep(hold);

                // 先松开：无论后面校验是否抛异常，右键都不能留在按下状态。
                m_injector.RunOnFrame(() =>
                {
                    m_injector.SetMouseHeld(ActionButton, false);
                    pressed = false;
                    watch.Stop();
                    return null;
                });

                // 等引擎真的跑过"能看到这次按下"的那一帧再取效果：
                // 松开与按下有可能落在**同一个** Dispatcher.BeforeFrame 批次里，那样
                // 帧体要到本批次之后才执行 ComponentPlayer.Update —— 立刻读会读不到变化。
                Thread.Sleep(SettleMs);

                Dictionary<string, object> after =
                    (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                    {
                        ComponentPlayer player = RequirePlayerReady();
                        return DescribeAfter(player, before);
                    });

                return ComposePulse(actionName, hold, before, after, watch.ElapsedMilliseconds);
            }
            finally
            {
                if (pressed)
                {
                    try
                    {
                        m_injector.RunOnFrame(() =>
                        {
                            m_injector.SetMouseHeld(ActionButton, false);
                            return null;
                        });
                    }
                    catch (Exception exception)
                    {
                        // 游戏线程已经不响应了：如实记日志，异常仍然向上抛给调用方。
                        Log.Warning("[CmdBridge] failed to release the injected right button after act."
                            + actionName + ": " + exception.GetType().Name + ": " + exception.Message);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 瞄准

        /// <summary>
        /// `act.aim start|release|cancel`。
        ///
        ///   start   —— 按住右键（PlayerInput.Aim 每帧续力）。给了 holdMs 就按住这么久再自动松开
        ///              （= Completed = 射出/扔出）；不给就一直按着，等 `release` 或 `cancel`。
        ///   release —— 松开右键，走 Completed（射出/扔出），并数一次投射物增量作为证据。
        ///   cancel  —— 松开但**不射出**：先 Aim(Cancelled) 归零蓄力（弓） / 放下击锤（火枪），
        ///              再清 `ComponentPlayer.m_aim`，最后松开右键（见类头第 3 点）。
        /// </summary>
        internal object Aim(string actionName, int holdMilliseconds)
        {
            m_injector.EnsureEnabled();
            string action = (actionName ?? string.Empty).Trim().ToLowerInvariant();
            switch (action)
            {
                case "start":
                    return AimStart(holdMilliseconds);
                case "release":
                    return AimRelease();
                case "cancel":
                    return AimCancel();
                default:
                    throw new BridgeCommandException("invalid_argument",
                        "act.aim needs action=start|release|cancel (got '"
                        + (actionName ?? string.Empty) + "').");
            }
        }

        private object AimStart(int holdMilliseconds)
        {
            int hold = NormalizeHold(holdMilliseconds);

            lock (m_sync)
            {
                // 自愈：引擎在失焦（`Mouse.Clear()`）或 `act.releaseall` / `act.mouse right up`
                // 之后可能已经不按着了，而簿记还留着 —— 那种"按着"是假的，直接清掉，
                // 否则调用方永远卡在 already_aiming 上（这正是"没有静默失败"要避免的另一面）。
                if (m_aimActive && !m_injector.IsMouseButtonHeldByInjection((int)ActionButton))
                    m_aimActive = false;

                if (m_aimActive)
                {
                    throw new BridgeCommandException("already_aiming",
                        "An aim started by act.aim is still held. Finish it first: "
                        + "act.aim release (fire) or act.aim cancel (lower without firing).");
                }
            }

            var watch = new Stopwatch();
            bool pressed = false;
            try
            {
                Dictionary<string, object> started =
                    (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                    {
                        ComponentPlayer player = RequirePlayerReady();
                        Dictionary<string, object> snapshot = DescribeAim(player, "start", hold);
                        m_injector.SetMouseHeld(ActionButton, true);
                        pressed = true;
                        watch.Start();
                        return snapshot;
                    });

                lock (m_sync)
                    m_aimActive = true;

                if (hold > 0)
                {
                    // 按住 hold 毫秒再松手：等价于真人拉弓那么久然后放箭。
                    Thread.Sleep(hold);
                    Dictionary<string, object> released = ReleaseCore("start", watch, hold);
                    // ReleaseCore 已经在帧首松开；不要再让 finally 重复松一次（也不会再"清簿记"）。
                    pressed = false;
                    released["holdMs"] = hold;
                    released["autoRelease"] = true;
                    return released;
                }

                // 没给 holdMs：保持按住。等引擎跑几帧再如实回报"到底开始瞄准了没有"，
                // 而不是让调用方以为按下去了就一定会拉弓。
                Thread.Sleep(SettleMs);
                Dictionary<string, object> aimAfter =
                    (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                        DescribeAimAfterStart(RequirePlayerReady()));
                watch.Stop();
                started["aimAfter"] = aimAfter;
                started["actualHoldMs"] = watch.ElapsedMilliseconds;
                started["aimingHeld"] = true;
                started["note"] =
                    "The right button is still held (PlayerInput.Aim is re-emitted every frame). "
                    + "Call act.aim release to fire, or act.aim cancel to lower without firing.";
                if (!IsTrue(aimAfter, "engineAimActive"))
                {
                    AddWarning(started,
                        "the engine does not report an aim in progress (ComponentPlayer.m_aim is null): "
                        + "the held item may not be aimable, or the aim was blocked (m_isAimBlocked / "
                        + "m_lastActionTime cooldown). See aimAfter for the exact reason.");
                }
                pressed = false; // 有意保持按住，不要让 finally 松开
                return started;
            }
            finally
            {
                if (pressed)
                {
                    try
                    {
                        m_injector.RunOnFrame(() =>
                        {
                            m_injector.SetMouseHeld(ActionButton, false);
                            return null;
                        });
                    }
                    catch (Exception exception)
                    {
                        Log.Warning("[CmdBridge] failed to release the injected right button after "
                            + "act.aim start: " + exception.GetType().Name + ": " + exception.Message);
                    }
                    lock (m_sync)
                        m_aimActive = false;
                }
            }
        }

        private object AimRelease()
        {
            lock (m_sync)
            {
                if (!m_aimActive)
                {
                    throw new BridgeCommandException("not_aiming",
                        "No aim started by act.aim is held, so act.aim release has nothing to release "
                        + "(the engine would fire nothing). Start one with act.aim start first; "
                        + "this command deliberately does not touch a real held right mouse button.");
                }
            }

            return ReleaseCore("release", Stopwatch.StartNew(), 0);
        }

        /// <summary>
        /// 松开右键并让引擎走 `AimState.Completed`（射出/扔出）。
        /// `phase` 只用于回包：`start`（`--hold` 自动松开）或 `release`（显式命令）。
        /// </summary>
        private Dictionary<string, object> ReleaseCore(string phase, Stopwatch watch, int hold)
        {
            List<string> warnings = new List<string>();

            Dictionary<string, object> atRelease =
                (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                {
                    // 先松开：无论后面校验是否抛异常，右键都不能留在按下状态。
                    m_injector.SetMouseHeld(ActionButton, false);
                    lock (m_sync)
                        m_aimActive = false;
                    watch.Stop();

                    ComponentPlayer player = RequirePlayerReady();
                    Dictionary<string, object> snapshot = DescribeAimState(player);
                    snapshot["projectilesBefore"] = ProjectileCount();
                    return snapshot;
                });

            int projectilesBefore = ToInt(atRelease, "projectilesBefore");
            atRelease.Remove("projectilesBefore");

            // Completed 在松开后的下一帧由 ComponentPlayer.Update 执行；等两帧再数投射物。
            Thread.Sleep(SettleMs);
            Dictionary<string, object> after =
                (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                {
                    ComponentPlayer player = RequirePlayerReady();
                    Dictionary<string, object> snapshot = DescribeAimState(player);
                    snapshot["projectiles"] = ProjectileCount();
                    return snapshot;
                });

            int projectilesAfter = ToInt(after, "projectiles");
            int created = projectilesAfter >= 0 && projectilesBefore >= 0
                ? projectilesAfter - projectilesBefore
                : -1;

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["completed"] = true;
            result["accepted"] = true;
            result["action"] = "act.aim";
            result["aimAction"] = phase;
            result["released"] = true;
            result["wasAiming"] = IsTrue(atRelease, "engineAimActive");
            result["input"] = DescribeAimInput(hold, phase == "start");
            result["heldItem"] = atRelease.TryGetValue("heldItem", out object heldAtRelease)
                ? heldAtRelease
                : null;
            result["aim"] = atRelease;
            result["aimAfter"] = after;
            result["projectiles"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["before"] = projectilesBefore,
                ["after"] = projectilesAfter,
                ["created"] = created,
                ["enginePath"] = "ComponentPlayer.Update -> ComponentMiner.Aim(AimState.Completed) "
                    + "(ComponentPlayer.cs:219-227)"
            };
            result["actualHoldMs"] = watch.ElapsedMilliseconds;

            if (!IsTrue(atRelease, "engineAimActive"))
            {
                warnings.Add("the engine did not report an aim in progress at release time, so nothing "
                    + "was completed: the held item may not be aimable, the aim may have been blocked, "
                    + "or the engine dropped the injected button when the window lost focus.");
            }
            if (created == 0)
            {
                warnings.Add("no projectile was created: a bow without arrows shows \"Load an arrow first\", "
                    + "a crossbow needs draw=15 and a loaded bolt, a musket needs gunpowder/wad/bullet, "
                    + "and a throwable needs the item in the active slot.");
            }

            result["warnings"] = warnings;
            result["note"] =
                "The right button was released through the engine input layer; ComponentPlayer runs "
                + "AimState.Completed on the next frame, which is what fires/throws.";
            return result;
        }

        private object AimCancel()
        {
            lock (m_sync)
            {
                if (!m_aimActive)
                {
                    throw new BridgeCommandException("not_aiming",
                        "No aim started by act.aim is held, so act.aim cancel has nothing to lower. "
                        + "Start one with act.aim start first; this command deliberately does not touch "
                        + "a real held right mouse button.");
                }
            }

            List<string> warnings = new List<string>();
            Dictionary<string, object> atCancel =
                (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                {
                    ComponentPlayer player = RequirePlayerReady();
                    Dictionary<string, object> snapshot = DescribeAimState(player);
                    snapshot["projectilesBefore"] = ProjectileCount();

                    // 1) 引擎自己的取消路径：归零蓄力 / 放下击锤 / 清掉瞄准起始时间
                    //    （SubsystemBowBlockBehavior.cs:93、SubsystemMusketBlockBehavior.cs:89）。
                    //    这一步失败不致命（关键是不让下一帧走 Completed），所以只记 warning。
                    bool cancelled = false;
                    try
                    {
                        Ray3 ray = new Ray3(
                            player.GameWidget.ActiveCamera.ViewPosition,
                            player.GameWidget.ActiveCamera.ViewDirection);
                        cancelled = player.ComponentMiner.Aim(ray, AimState.Cancelled);
                    }
                    catch (Exception exception)
                    {
                        warnings.Add("ComponentMiner.Aim(AimState.Cancelled) threw "
                            + exception.GetType().Name + ": " + exception.Message
                            + " (the aim was still cleared and lowered)");
                    }
                    snapshot["cancelledEngineState"] = cancelled;

                    // 2) 清掉"进行中的瞄准"。不清这一行，下一帧右键一松开就会走 Completed 把箭射出去
                    //    （ComponentPlayer.cs:219-227 的 else 分支只看 m_aim.HasValue）。
                    snapshot["clearedPendingAim"] = ClearPendingAim(player);

                    // 3) 松开右键。
                    m_injector.SetMouseHeld(ActionButton, false);
                    lock (m_sync)
                        m_aimActive = false;
                    return snapshot;
                });

            int projectilesBefore = ToInt(atCancel, "projectilesBefore");
            atCancel.Remove("projectilesBefore");

            Thread.Sleep(SettleMs);
            Dictionary<string, object> after =
                (Dictionary<string, object>)m_injector.RunOnFrame(() =>
                {
                    ComponentPlayer player = RequirePlayerReady();
                    Dictionary<string, object> snapshot = DescribeAimState(player);
                    snapshot["projectiles"] = ProjectileCount();
                    return snapshot;
                });

            int projectilesAfter = ToInt(after, "projectiles");
            int created = projectilesAfter >= 0 && projectilesBefore >= 0
                ? projectilesAfter - projectilesBefore
                : -1;

            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["completed"] = true;
            result["accepted"] = true;
            result["action"] = "act.aim";
            result["aimAction"] = "cancel";
            result["released"] = true;
            result["cancelledEngineState"] = atCancel["cancelledEngineState"];
            result["clearedPendingAim"] = atCancel["clearedPendingAim"];
            result["wasAiming"] = IsTrue(atCancel, "engineAimActive");
            result["input"] = DescribeAimInput(0, false);
            result["heldItem"] = atCancel.TryGetValue("heldItem", out object heldAtCancel)
                ? heldAtCancel
                : null;
            result["aim"] = atCancel;
            result["aimAfter"] = after;
            result["projectiles"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["before"] = projectilesBefore,
                ["after"] = projectilesAfter,
                ["created"] = created,
                ["enginePath"] = "AimState.Cancelled + ComponentPlayer.m_aim = null "
                    + "(same as ComponentPlayer.cs:211-214)"
            };

            if (created > 0)
            {
                warnings.Add("cancel created " + created + " projectile(s): the aim had already completed "
                    + "before the cancel reached the game thread. Check aimAfter.");
            }

            result["warnings"] = warnings;
            result["note"] =
                "Cancelled without firing: AimState.Cancelled resets the draw / uncocks the musket, "
                + "ComponentPlayer.m_aim is cleared so releasing cannot run AimState.Completed, "
                + "then the virtual right button is released.";
            return result;
        }

        // ---------------------------------------------------------------- 状态描述

        /// <summary>
        /// `act.aim start` 按下之前的快照：按住会喂给 `PlayerInput.Aim`，引擎据此跑
        /// `ComponentMiner.Aim(InProgress)`（弓/弩/火枪/投掷物的蓄力都在这里）。
        /// 同时把"按住了也未必会瞄准"的原因（不可瞄 / 被 m_isAimBlocked 挡住 / 冷却未过）写进 warnings。
        /// </summary>
        private static Dictionary<string, object> DescribeAim(
            ComponentPlayer player, string actionName, int hold)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            ComponentMiner miner = player.ComponentMiner;
            Camera camera = player.GameWidget.ActiveCamera;
            Ray3 ray = new Ray3(camera.ViewPosition, camera.ViewDirection);
            var warnings = new List<string>();

            Dictionary<string, object> aim = DescribeAimState(player);
            double cooldownMs = -1.0;
            if (aim.ContainsKey("cooldownMs"))
            {
                try { cooldownMs = Convert.ToDouble(aim["cooldownMs"]); }
                catch (Exception) { cooldownMs = -1.0; }
            }

            if (!IsTrue(aim, "isAimable"))
            {
                warnings.Add("the held item is not aimable (Block.IsAimable == false): PlayerInput.Aim "
                    + "is still injected, but ComponentMiner.Aim ignores non-aimable items "
                    + "(ComponentMiner.cs:352-372). Hold a bow (191) / crossbow (200) / musket (212) / "
                    + "spear and retry.");
            }
            if (IsTrue(aim, "aimBlocked"))
            {
                warnings.Add("ComponentPlayer.m_isAimBlocked is true: the engine will not start a new "
                    + "aim until the aim input has been absent for at least one frame "
                    + "(ComponentPlayer.cs:185, 220-221). Release once (act.releaseall) and retry.");
            }
            if (cooldownMs > 0.0)
            {
                warnings.Add("the engine aim cooldown is still " + cooldownMs.ToString("0")
                    + " ms (ComponentPlayer.cs:183: 0.1s in creative, 1.4s otherwise; it starts after "
                    + "an aim completes or a dig succeeds). Waiting or aiming anyway both work: the "
                    + "right button is held until act.aim release.");
            }

            result["completed"] = true;
            result["accepted"] = true;
            result["action"] = "act.aim";
            result["aimAction"] = actionName;
            result["input"] = DescribeAimInput(hold, hold > 0);
            result["engineOrder"] = new List<string>
            {
                "ComponentMiner.Aim(AimState.InProgress) every frame while the right button is held "
                    + "(ComponentPlayer.cs:183-200)",
                "ComponentMiner.Aim(AimState.Completed) when it is released (ComponentPlayer.cs:219-227)"
            };
            result["heldItem"] = DescribeHeldItem(miner);
            result["target"] = DescribeInteractionTarget(miner, ray);
            result["aim"] = aim;
            result["warnings"] = warnings;
            result["hint"] = hold > 0
                ? "The right button is held for " + hold + " ms and then auto-released: the release is "
                    + "what runs AimState.Completed (fire/throw)."
                : "The right button stays held until act.aim release (fire) or act.aim cancel (lower).";
            return result;
        }

        /// <summary>按下之前（同一帧首）的快照：手持物、引擎会命中的交互目标、周围格子、分支预测。</summary>
        private static Dictionary<string, object> DescribeAction(
            ComponentPlayer player, string actionName, int hold)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            ComponentMiner miner = player.ComponentMiner;
            Camera camera = player.GameWidget.ActiveCamera;
            Ray3 ray = new Ray3(camera.ViewPosition, camera.ViewDirection);

            Dictionary<string, object> held = DescribeHeldItem(miner);
            Dictionary<string, object> target = DescribeInteractionTarget(miner, ray);
            var warnings = new List<string>();

            string predicted = PredictBranch(held, target, out string reason);
            ExplainExpectation(actionName, predicted, warnings);

            result["completed"] = true;
            result["accepted"] = true;
            result["action"] = "act." + actionName;
            result["input"] = DescribeActionInput(hold);
            result["engineOrder"] = EngineOrder();
            result["predictedBranch"] = predicted;
            result["predictedReason"] = reason;
            result["heldItem"] = held;
            result["target"] = target;
            result["cells"] = SnapshotCells(target);
            result["aim"] = DescribeAimState(player);
            result["warnings"] = warnings;
            if (predicted == "none")
            {
                result["hint"] =
                    "No engine branch can handle this right click at the current crosshair: the held "
                    + "item has no OnUse behavior, the crosshair is not on an interactive block, and the "
                    + "held item is not placeable. A real player's right click would do nothing either. "
                    + "Aim at a block (act.lookat) or select another item (act.wheel).";
            }
            return result;
        }

        /// <summary>松开之后（同一帧首）再取一次：手持物与周围格子，用于 diff 出"到底动了什么"。</summary>
        private static Dictionary<string, object> DescribeAfter(
            ComponentPlayer player, Dictionary<string, object> before)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["heldItem"] = DescribeHeldItem(player.ComponentMiner);
            result["cells"] = SnapshotCells(
                before["target"] as Dictionary<string, object>);
            return result;
        }

        private static Dictionary<string, object> ComposePulse(
            string actionName, int hold, Dictionary<string, object> before,
            Dictionary<string, object> after, long actualHoldMs)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["completed"] = true;
            result["accepted"] = true;
            result["action"] = "act." + actionName;
            result["input"] = before["input"];
            result["holdMs"] = hold;
            result["actualHoldMs"] = actualHoldMs;
            result["engineOrder"] = before["engineOrder"];
            result["predictedBranch"] = before["predictedBranch"];
            result["predictedReason"] = before["predictedReason"];
            result["heldItem"] = before.TryGetValue("heldItem", out object heldBefore)
                ? heldBefore
                : null;
            result["heldItemAfter"] = after.TryGetValue("heldItem", out object heldAfter)
                ? heldAfter
                : null;
            result["target"] = before["target"];
            result["cells"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["before"] = before["cells"],
                ["after"] = after["cells"],
                ["changed"] = DiffCells(
                    before["cells"] as List<Dictionary<string, object>>,
                    after["cells"] as List<Dictionary<string, object>>)
            };
            result["aim"] = before["aim"];
            result["warnings"] = before["warnings"];
            if (before.ContainsKey("hint"))
                result["hint"] = before["hint"];
            result["note"] =
                "Injected one virtual right-button edge (PlayerInput.Interact) and released it. "
                + "The engine's own branch order (Use -> Interact -> Place) decides what actually "
                + "happened; predictedBranch is a prediction, cells.changed is the observation.";
            return result;
        }

        private static Dictionary<string, object> DescribeActionInput(int hold)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["virtual"] = true,
                ["button"] = "right",
                ["fields"] = new List<string> { "PlayerInput.Interact", "PlayerInput.Aim" },
                ["engineRead"] = "ComponentInput.UpdateInputFromMouseAndKeyboard: "
                    + "Aim <- IsMouseButtonDown(Right), Interact <- IsMouseButtonDownOnce(Right) "
                    + "(ComponentInput.cs:185-186)",
                ["holdMs"] = hold,
                ["frameStart"] = true,
                ["realInputUntouched"] = true
            };
        }

        private static Dictionary<string, object> DescribeAimInput(int hold, bool autoRelease)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["virtual"] = true,
                ["button"] = "right",
                ["fields"] = new List<string> { "PlayerInput.Aim" },
                ["engineRead"] = "ComponentInput.UpdateInputFromMouseAndKeyboard: "
                    + "Aim <- IsMouseButtonDown(Right) (held every frame) (ComponentInput.cs:185)",
                ["holdMs"] = hold,
                ["autoRelease"] = autoRelease,
                ["frameStart"] = true,
                ["realInputUntouched"] = true
            };
        }

        private static List<string> EngineOrder()
        {
            return new List<string>
            {
                "ComponentMiner.Use (ComponentPlayer.cs:153)",
                "ComponentMiner.Interact (ComponentPlayer.cs:158)",
                "ComponentMiner.Place (ComponentPlayer.cs:160)"
            };
        }

        /// <summary>`act.aim start` 之后再看一眼：引擎真的进入瞄准了吗？蓄力到多少了？</summary>
        private static Dictionary<string, object> DescribeAimAfterStart(ComponentPlayer player)
        {
            Dictionary<string, object> state = DescribeAimState(player);
            return state;
        }

        private static Dictionary<string, object> DescribeAimState(ComponentPlayer player)
        {
            var state = new Dictionary<string, object>(StringComparer.Ordinal);
            ComponentMiner miner = player.ComponentMiner;
            int value = miner.ActiveBlockValue;
            int contents = Terrain.ExtractContents(value);
            int data = Terrain.ExtractData(value);
            Block block = BlocksManager.Blocks[contents];

            // 两个读数必须分开：
            //   · inputAimActive  —— 引擎输入层这一帧有没有 `PlayerInput.Aim`（我们的右键在按着就有）；
            //   · engineAimActive —— `ComponentPlayer.m_aim` 是否非空，也就是引擎**真的**在瞄准
            //     （不可瞄 / m_isAimBlocked / 冷却未过时，上面是 true、这里是 false）。
            state["inputAimActive"] = HasAimInput(player);
            state["engineAimActive"] = HasPendingAim(player);
            state["aimBlocked"] = ReadBool(player, InputWhitelist.ComponentPlayerAimBlocked);
            state["cooldownMs"] = RemainingAimCooldownMs(player);
            state["activeBlockValue"] = value;
            state["contents"] = contents;
            state["blockType"] = block != null ? block.GetType().Name : null;
            state["isAimable"] = block != null && block.IsAimable;

            // 蓄力/装填读数：这是"拉弓/上弦/上膛"最直接的证据（都只读）。
            if (block is BowBlock)
                state["bowDraw"] = BowBlock.GetDraw(data);
            else if (block is CrossbowBlock)
                state["crossbowDraw"] = CrossbowBlock.GetDraw(data);
            else if (block is MusketBlock)
            {
                state["musketHammerCocked"] = MusketBlock.GetHammerState(data);
                state["musketLoadState"] = MusketBlock.GetLoadState(data).ToString();
            }
            return state;
        }

        /// <summary>手持物（当前快捷栏格子）的静态信息 + 是否有 OnUse 行为。</summary>
        private static Dictionary<string, object> DescribeHeldItem(ComponentMiner miner)
        {
            var item = new Dictionary<string, object>(StringComparer.Ordinal);
            IInventory inventory = miner.Inventory;
            if (inventory == null)
            {
                item["available"] = false;
                return item;
            }

            int slot = inventory.ActiveSlotIndex;
            if (slot < 0 || slot >= inventory.SlotsCount)
            {
                item["available"] = false;
                item["reason"] = "active slot index " + slot + " is out of range";
                return item;
            }

            int value = inventory.GetSlotValue(slot);
            int contents = Terrain.ExtractContents(value);
            Block block = BlocksManager.Blocks[contents];

            item["available"] = true;
            item["activeSlot"] = slot;
            item["value"] = value;
            item["contents"] = contents;
            item["count"] = inventory.GetSlotCount(slot);
            item["blockType"] = block != null ? block.GetType().Name : null;
            item["isAimable"] = block != null && block.IsAimable;
            item["isPlaceable"] = block != null && block.IsPlaceable;
            item["hasOnUseBehavior"] = HasBehaviorOverride(contents, "OnUse");
            return item;
        }

        /// <summary>
        /// 引擎此刻**会**交互到哪个方块：与 `ComponentPlayer` 用的是同一个调用
        /// （`ComponentMiner.Raycast&lt;TerrainRaycastResult&gt;(ray, RaycastMode.Interaction)`），
        /// 所以"预测"和"真发生"不会因为射线参数不同而打架。只读。
        /// </summary>
        private static Dictionary<string, object> DescribeInteractionTarget(ComponentMiner miner, Ray3 ray)
        {
            var info = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                TerrainRaycastResult? hit = miner.Raycast<TerrainRaycastResult>(ray, RaycastMode.Interaction);
                if (!hit.HasValue)
                {
                    info["kind"] = "none";
                    return info;
                }

                TerrainRaycastResult result = hit.Value;
                int contents = Terrain.ExtractContents(result.Value);
                Block block = BlocksManager.Blocks[contents];
                info["kind"] = "block";
                info["cell"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = result.CellFace.X,
                    ["y"] = result.CellFace.Y,
                    ["z"] = result.CellFace.Z
                };
                info["face"] = result.CellFace.Face;
                info["value"] = result.Value;
                info["contents"] = contents;
                info["blockType"] = block != null ? block.GetType().Name : null;
                info["distance"] = result.Distance;
                info["hitPoint"] = PlayerObserver.Vector3ToDictionary(result.HitPoint());
                info["hasOnInteractBehavior"] = HasBehaviorOverride(contents, "OnInteract");
            }
            catch (Exception exception)
            {
                info["kind"] = "error";
                info["error"] = exception.GetType().Name + ": " + exception.Message;
            }
            return info;
        }

        /// <summary>按引擎顺序预测右键会落到哪一步（与 ComponentPlayer.cs:151-181 一一对应）。</summary>
        private static string PredictBranch(
            Dictionary<string, object> held, Dictionary<string, object> target, out string reason)
        {
            bool hasUse = IsTrue(held, "hasOnUseBehavior");
            bool placeable = IsTrue(held, "isPlaceable");
            string kind = target != null && target.ContainsKey("kind")
                ? Convert.ToString(target["kind"]) : "none";
            bool hasInteract = IsTrue(target, "hasOnInteractBehavior");

            if (hasUse)
            {
                reason = "the held item has a SubsystemBlockBehavior.OnUse override, and "
                    + "ComponentMiner.Use runs first (ComponentPlayer.cs:153)";
                return "use";
            }
            if (kind == "block" && hasInteract)
            {
                reason = "the crosshair hits a block whose behaviors override OnInteract, and Use "
                    + "returned no handler (ComponentPlayer.cs:158)";
                return "interact";
            }
            if (kind == "block" && placeable)
            {
                reason = "the held item is placeable and the crosshair hits a block "
                    + "(ComponentMiner.Place, ComponentPlayer.cs:160)";
                return "place";
            }
            reason = "no engine branch matches: no OnUse behavior on the held item, the crosshair "
                + "is not on an interactable block, and the held item is not placeable";
            return "none";
        }

        /// <summary>把"命令语义 vs 引擎顺序"的差异写进 warnings，避免"place 却变成了 use"这种误解。</summary>
        private static void ExplainExpectation(string actionName, string predicted, List<string> warnings)
        {
            if (actionName == "use" && predicted != "use")
            {
                warnings.Add("act.use injects the engine's right-click; the held item has no OnUse "
                    + "behavior, so the engine falls through to '" + predicted + "' "
                    + "(ComponentPlayer.cs:151-181). That is exactly what a real right click does.");
            }
            else if (actionName == "interact" && predicted == "use")
            {
                warnings.Add("the held item handles Use first, so the engine runs OnUse instead of "
                    + "OnInteract (ComponentPlayer.cs:153). Switch to a non-use item to interact with "
                    + "the block, or use act.use for the item.");
            }
            else if (actionName == "place" && predicted != "place")
            {
                warnings.Add("act.place predicts '" + predicted + "', not 'place': the engine runs "
                    + "Use and Interact before Place (ComponentPlayer.cs:151-181). Select a plain "
                    + "placeable block if you need a pure placement.");
            }
        }

        // ---------------------------------------------------------------- 格子快照

        /// <summary>
        /// 交互目标格 + 六个邻居的**真实地形值**（`Terrain.GetCellValue`）。
        /// 目的：让"放置/开关门/按钮"这类动作有一个与引擎内部状态无关的客观读据
        /// （放置会改邻居格，门/按钮会改命中格的 data）。
        /// </summary>
        private static List<Dictionary<string, object>> SnapshotCells(
            Dictionary<string, object> target)
        {
            var cells = new List<Dictionary<string, object>>();
            SubsystemTerrain terrain = GameManager.Project != null
                ? GameManager.Project.FindSubsystem<SubsystemTerrain>(false) : null;
            if (terrain == null || terrain.Terrain == null || target == null)
                return cells;
            if (!string.Equals(Convert.ToString(target.ContainsKey("kind") ? target["kind"] : null),
                "block", StringComparison.Ordinal))
            {
                return cells;
            }

            var cell = target["cell"] as Dictionary<string, object>;
            if (cell == null)
                return cells;
            int x = Convert.ToInt32(cell["x"]);
            int y = Convert.ToInt32(cell["y"]);
            int z = Convert.ToInt32(cell["z"]);

            AddCell(cells, terrain, x, y, z);
            for (int face = 0; face < 6; face++)
            {
                Point3 offset = CellFace.FaceToPoint3(face);
                AddCell(cells, terrain, x + offset.X, y + offset.Y, z + offset.Z);
            }
            return cells;
        }

        private static void AddCell(
            List<Dictionary<string, object>> cells, SubsystemTerrain terrain, int x, int y, int z)
        {
            cells.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = x,
                ["y"] = y,
                ["z"] = z,
                ["value"] = terrain.Terrain.GetCellValue(x, y, z)
            });
        }

        private static List<Dictionary<string, object>> DiffCells(
            List<Dictionary<string, object>> before, List<Dictionary<string, object>> after)
        {
            var changed = new List<Dictionary<string, object>>();
            if (before == null || after == null)
                return changed;

            for (int i = 0; i < before.Count; i++)
            {
                Dictionary<string, object> b = before[i];
                Dictionary<string, object> a = FindCell(after, b);
                int beforeValue = Convert.ToInt32(b["value"]);
                int afterValue = a != null ? Convert.ToInt32(a["value"]) : 0;
                if (a != null && afterValue == beforeValue)
                    continue;

                int beforeContents = Terrain.ExtractContents(beforeValue);
                int afterContents = Terrain.ExtractContents(afterValue);
                changed.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = b["x"],
                    ["y"] = b["y"],
                    ["z"] = b["z"],
                    ["before"] = beforeValue,
                    ["after"] = afterValue,
                    ["beforeContents"] = beforeContents,
                    ["afterContents"] = afterContents,
                    ["beforeBlock"] = BlockName(beforeContents),
                    ["afterBlock"] = BlockName(afterContents)
                });
            }
            return changed;
        }

        private static Dictionary<string, object> FindCell(
            List<Dictionary<string, object>> cells, Dictionary<string, object> wanted)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                Dictionary<string, object> candidate = cells[i];
                if (Convert.ToInt32(candidate["x"]) == Convert.ToInt32(wanted["x"]) &&
                    Convert.ToInt32(candidate["y"]) == Convert.ToInt32(wanted["y"]) &&
                    Convert.ToInt32(candidate["z"]) == Convert.ToInt32(wanted["z"]))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static string BlockName(int contents)
        {
            try
            {
                Block block = BlocksManager.Blocks[contents];
                return block != null ? block.GetType().Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- 引擎状态读取

        private static int ProjectileCount()
        {
            try
            {
                SubsystemProjectiles projectiles = GameManager.Project != null
                    ? GameManager.Project.FindSubsystem<SubsystemProjectiles>(false) : null;
                return projectiles != null ? projectiles.Projectiles.Count : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>输入层这一帧有没有 `PlayerInput.Aim`（右键按住期间恒为 true，哪怕引擎挡着不瞄准）。</summary>
        private static bool HasAimInput(ComponentPlayer player)
        {
            try
            {
                return player.ComponentInput.PlayerInput.Aim.HasValue;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 引擎是否**真的**在瞄准：`ComponentPlayer.m_aim` 非空。
        /// 它只在 `ComponentPlayer.Update` 的瞄准分支里被赋值（ComponentPlayer.cs:189），
        /// 是"不可瞄 / 被 m_isAimBlocked 挡住 / 冷却未过"时唯一可信的判据。
        /// </summary>
        private static bool HasPendingAim(ComponentPlayer player)
        {
            return ReadField(player, InputWhitelist.ComponentPlayerAim) != null;
        }

        /// <summary>
        /// 引擎的"上一步动作"冷却（ComponentPlayer.cs:183：`GameTime - m_lastActionTime > num2`，
        /// num2 = 创造 0.1s / 其他 1.4s）。剩余大于 0 时 `act.aim start` 会被引擎忽略。
        /// </summary>
        private static double RemainingAimCooldownMs(ComponentPlayer player)
        {
            try
            {
                object raw = ReadField(player, InputWhitelist.ComponentPlayerLastActionTime);
                if (raw == null)
                    return -1.0;
                double lastActionTime = Convert.ToDouble(raw);
                SubsystemTime time = GameManager.Project != null
                    ? GameManager.Project.FindSubsystem<SubsystemTime>(false) : null;
                if (time == null)
                    return -1.0;
                SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
                float required = gameInfo != null &&
                    gameInfo.WorldSettings.GameMode == GameMode.Creative ? 0.1f : 1.4f;
                double remaining = required - (time.GameTime - lastActionTime);
                return Math.Max(0.0, remaining * 1000.0);
            }
            catch (Exception)
            {
                return -1.0;
            }
        }

        private static bool ReadBool(object target, string fieldName)
        {
            object value = ReadField(target, fieldName);
            return value is bool && (bool)value;
        }

        private static object ReadField(object target, string fieldName)
        {
            try
            {
                return Fields.GetParentField(target, fieldName, target.GetType());
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 清掉"进行中的瞄准"（`ComponentPlayer.m_aim`，ComponentPlayer.cs:26）。
        /// 只用于 `act.aim cancel`：引擎没有取消瞄准的输入，松开只会走 Completed（射出）。
        /// 读取失败时**不静默**：抛错并让调用方知道这次取消没有完成，避免"以为收弓了结果一松手就射"。
        /// </summary>
        private static bool ClearPendingAim(ComponentPlayer player)
        {
            try
            {
                object pending = Fields.GetParentField(
                    player, InputWhitelist.ComponentPlayerAim, typeof(ComponentPlayer));
                if (pending == null)
                    return false;
                Fields.ModifyParentField(
                    player, InputWhitelist.ComponentPlayerAim, null, typeof(ComponentPlayer));
                return true;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("aim_state_unavailable",
                    "Cannot clear ComponentPlayer.m_aim (" + exception.GetType().Name + ": "
                    + exception.Message + "). act.aim cancel refused to release the button, because "
                    + "releasing with m_aim still set would run AimState.Completed and fire. "
                    + "The virtual right button is still held; use act.aim release to fire, or fix the "
                    + "reflection access first.");
            }
        }

        // ---------------------------------------------------------------- 前置条件

        /// <summary>
        /// 玩家/引擎是否真的会处理这份输入。`ComponentInput.Update` 会在以下情况把整份
        /// `PlayerInput` 清零或丢掉（ComponentInput.cs:91-107）：世界没加载、玩家未就绪、
        /// 死亡、睡觉、相机禁用实体控制、窗口不活跃。任何一个命中都**明确报错**，
        /// 绝不"命令返回成功但游戏里什么也没发生"。
        /// </summary>
        private static ComponentPlayer RequirePlayerReady()
        {
            if (GameManager.Project == null)
                throw new BridgeCommandException("world_not_loaded", "No world is loaded.");

            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                throw new BridgeCommandException("player_not_found", "No player is available.");

            ComponentPlayer player = players.ComponentPlayers[0];
            if (!player.PlayerData.IsReadyForPlaying)
            {
                throw new BridgeCommandException("player_not_ready",
                    "The local player is not ready for playing yet (PlayerData.IsReadyForPlaying == "
                    + "false): ComponentInput.Update discards every PlayerInput until it is true "
                    + "(ComponentInput.cs:91-94).");
            }
            if (player.ComponentHealth != null && player.ComponentHealth.Health <= 0f)
            {
                throw new BridgeCommandException("player_not_ready",
                    "The local player is dead (ComponentHealth.Health <= 0): ComponentInput.Update "
                    + "keeps only camera/menu fields and drops every action input "
                    + "(ComponentInput.cs:95-107).");
            }
            if (player.ComponentSleep != null && player.ComponentSleep.SleepFactor > 0f)
            {
                throw new BridgeCommandException("player_not_ready",
                    "The local player is sleeping (ComponentSleep.SleepFactor > 0): action input is "
                    + "dropped by ComponentInput.Update (ComponentInput.cs:95-107).");
            }

            GameWidget gameWidget = player.GameWidget;
            Camera camera = gameWidget != null ? gameWidget.ActiveCamera : null;
            if (camera == null)
                throw new BridgeCommandException("player_not_ready", "The player has no active camera.");
            if (!camera.IsEntityControlEnabled)
            {
                throw new BridgeCommandException("player_not_ready",
                    "The active camera (" + camera.GetType().Name + ") has IsEntityControlEnabled == "
                    + "false, so ComponentInput.Update drops all action input "
                    + "(ComponentInput.cs:95-107).");
            }

            WidgetInput widgetInput = gameWidget.Input;
            if (widgetInput == null || (widgetInput.Devices & WidgetInputDevice.Mouse) == 0)
            {
                throw new BridgeCommandException("input_channel_unavailable",
                    "This client's GameWidget input does not include WidgetInputDevice.Mouse "
                    + "(Devices=" + (widgetInput != null ? widgetInput.Devices.ToString() : "null")
                    + "). PlayerInput for use/interact/place/aim is derived from the mouse channel "
                    + "(ComponentInput.cs:185-186), so a virtual right button cannot reach it on this "
                    + "input-device configuration.");
            }

            if (player.ComponentGui != null && player.ComponentGui.ModalPanelWidget != null)
            {
                throw new BridgeCommandException("ui_modal_open",
                    "A modal panel is open (" + player.ComponentGui.ModalPanelWidget.GetType().Name
                    + "). ComponentInput switches to cursor mode and never writes "
                    + "Interact/Aim while a modal is up (ComponentInput.cs:143-152). Close the panel "
                    + "first (act.key escape / ui.click) and retry.");
            }
            if (DialogsManager.HasDialogs(player.GuiWidget))
            {
                throw new BridgeCommandException("ui_modal_open",
                    "A dialog is open. ComponentInput switches to cursor mode and never writes "
                    + "Interact/Aim while dialogs are up (ComponentInput.cs:143-152). Close the dialog "
                    + "first (act.key escape / ui.click) and retry.");
            }

            // 世界必须正在被更新：Project 的更新入口是 `GameScreen.Update -> GameManager.UpdateProject`
            // （GameScreen.cs:53），换了别的界面（Settings/Players/Help…）时 GameWidget 不再 Update、
            // ComponentInput 根本不会跑 —— 注入会是一个彻底的静默空操作。这里如实拒绝。
            Screen currentScreen = ScreensManager.CurrentScreen;
            if (!(currentScreen is GameScreen))
            {
                throw new BridgeCommandException("world_not_active",
                    "The game world is not the active screen (ScreensManager.CurrentScreen = "
                    + (currentScreen != null ? currentScreen.GetType().Name : "null") + "). "
                    + "GameScreen.Update -> GameManager.UpdateProject is what drives ComponentInput "
                    + "(GameScreen.cs:53), so the injection would never be read. Return to the game "
                    + "screen and retry.");
            }

            // 引擎只在"窗口活跃"时才认输入；失焦时 ComponentInput 会把整份 PlayerInput 清零。
            // 焦点策略（focus.detach / auto）会在帧首把 Window.m_state 置成 Active —— 这里读到的
            // 就是引擎本帧真正会用的状态，不做任何"我猜它应该活跃"的假设。
            if (!Window.IsActive)
            {
                throw new BridgeCommandException("window_not_active",
                    "The engine treats the game window as inactive (Window.IsActive == false), so "
                    + "ComponentInput.Update discards all PlayerInput (ComponentInput.cs:91-94) and "
                    + "this injection would be a silent no-op. Use focus.detach (or focus.auto with the "
                    + "real focus outside the game) so the mod keeps the engine's own state active; "
                    + "we never steal the real window or the real mouse.");
            }

            return player;
        }

        private static bool HasBehaviorOverride(int contents, string methodName)
        {
            try
            {
                SubsystemBlockBehaviors behaviors =
                    GameManager.Project != null
                        ? GameManager.Project.FindSubsystem<SubsystemBlockBehaviors>(false) : null;
                if (behaviors == null)
                    return false;
                SubsystemBlockBehavior[] list = behaviors.GetBlockBehaviors(contents);
                for (int i = 0; i < list.Length; i++)
                {
                    MethodInfo method = list[i].GetType().GetMethod(
                        methodName, BindingFlags.Public | BindingFlags.Instance);
                    if (method != null && method.DeclaringType != typeof(SubsystemBlockBehavior))
                        return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // ---------------------------------------------------------------- 工具

        private static int NormalizeHold(int holdMilliseconds)
        {
            if (holdMilliseconds < 0)
                throw new BridgeCommandException("invalid_argument", "holdMs must be >= 0.");
            return Math.Min(holdMilliseconds, MaxHoldMs);
        }

        private static bool IsTrue(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key))
                return false;
            object value = map[key];
            return value is bool && (bool)value;
        }

        private static int ToInt(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key))
                return -1;
            try
            {
                return Convert.ToInt32(map[key]);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static void AddWarning(Dictionary<string, object> map, string warning)
        {
            if (map == null || string.IsNullOrEmpty(warning))
                return;
            var warnings = map.ContainsKey("warnings")
                ? map["warnings"] as List<string> : null;
            if (warnings == null)
            {
                warnings = new List<string>();
                map["warnings"] = warnings;
            }
            warnings.Add(warning);
        }

        private static IModParentField Fields
        {
            get { return ModManager.Instance.ModParentField; }
        }
    }
}

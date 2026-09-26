using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>
    /// 运行时目标解析（§4.9：**Laya 给枚举，C# 给具体值**）。
    ///
    /// verb 的参数里只允许出现"语义目标"（`aim` / `entity` / `first_food` / `self` / 显式坐标），
    /// 具体是哪一格、哪一个实体，由实现方用**只读观察**在**执行那一刻**现解析。
    /// 于是：
    ///   · 模型永远不见坐标；
    ///   · 同一状态同一次执行得到同一个目标（可复现，复盘能对账）；
    ///   · 动作层不需要认识游戏类型（临时工程里塞个假实现就能自检）。
    /// </summary>
    public interface IActionTargetResolver
    {
        /// <summary>解析一个语义目标。失败时返回 null 并给出原因。</summary>
        ActionTarget Resolve(string target, out string error);
    }

    /// <summary>解析出来的具体目标（坐标格 / 绝对角度 / 实体名）。</summary>
    public sealed class ActionTarget
    {
        public ActionTarget(string kind)
        {
            Kind = kind;
        }

        /// <summary>`cell`（方块格）/ `point`（世界点）/ `entity`（角色名）/ `none`。</summary>
        public string Kind { get; }

        public int X;
        public int Y;
        public int Z;

        public float PointX, PointY, PointZ;

        /// <summary>实体名（`entity` 类目标）。</summary>
        public string Name;

        /// <summary>看向点（cell 类目标默认取格中心；entity 类由实现方给眼睛高度）。</summary>
        public bool HasLookPoint;
        public float LookX, LookY, LookZ;

        public static ActionTarget Cell(int x, int y, int z)
        {
            var target = new ActionTarget("cell") { X = x, Y = y, Z = z };
            target.HasLookPoint = true;
            target.LookX = x + 0.5f;
            target.LookY = y + 0.5f;
            target.LookZ = z + 0.5f;
            return target;
        }

        public static ActionTarget Point(float x, float y, float z)
        {
            var target = new ActionTarget("point") { PointX = x, PointY = y, PointZ = z };
            target.HasLookPoint = true;
            target.LookX = x;
            target.LookY = y;
            target.LookZ = z;
            return target;
        }

        public static ActionTarget Entity(string name, float x, float y, float z)
        {
            var target = new ActionTarget("entity") { Name = name };
            target.HasLookPoint = true;
            target.LookX = x;
            target.LookY = y;
            target.LookZ = z;
            return target;
        }

        public override string ToString()
        {
            if (Kind == "cell")
                return "cell(" + X + "," + Y + "," + Z + ")";
            if (Kind == "entity")
                return "entity(" + (Name ?? "?") + ")";
            if (Kind == "point")
                return "point(" + PointX.ToString("0.0") + "," + PointY.ToString("0.0") + "," + PointZ.ToString("0.0") + ")";
            return Kind ?? "?";
        }
    }

    /// <summary>
    /// verb 词表与编译器（动作层的心脏）。**不依赖游戏类型**：只产出 <see cref="ActionClip"/>。
    ///
    /// 三条纪律（对齐 doc/laya-action-layer-plan.md §3）：
    ///   1. 每个 verb 只做"人能做的输入组合"，绝不写生命/背包/方块/位置/时间（优势≠特权）；
    ///   2. 参数**只允许语义目标**，具体值由 <see cref="IActionTargetResolver"/> 现解析（§4.9）；
    ///   3. 编译期就把参数错误报成稳定错误码（<see cref="ActionErrorCodes"/>），不留给运行时猜。
    /// </summary>
    public static class ScriptCompiler
    {
        // ---------------------------------------------------------------- 键名（与 CmdBridgeInput 的键名一致）

        public const string KeyForward = "W";
        public const string KeyBack = "S";
        public const string KeyLeft = "A";
        public const string KeyRight = "D";
        public const string KeyJump = "Space";
        public const string KeySneak = "Shift";
        public const string KeyMount = "R";
        public const string KeyCreativeFly = "F";
        public const string KeyInventory = "E";
        public const string KeyClothing = "C";
        public const string KeyDrop = "Q";
        public const string KeyEditItem = "G";

        /// <summary>快捷栏槽位 → 键名（槽位 10 = 数字 0，与游戏一致）。</summary>
        public static string SlotKey(int slot)
        {
            if (slot < 1 || slot > 10)
                return null;
            return "Number" + (slot % 10).ToString(CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- verb 表

        private static readonly Dictionary<string, ActionVerbSpec> s_verbs =
            new Dictionary<string, ActionVerbSpec>(StringComparer.OrdinalIgnoreCase);

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

        public static IReadOnlyList<string> VerbNames
        {
            get
            {
                EnsureInitialized();
                var names = new List<string>(s_verbs.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names;
            }
        }

        public static bool TryGetVerb(string name, out ActionVerbSpec spec)
        {
            EnsureInitialized();
            spec = null;
            if (string.IsNullOrEmpty(name))
                return false;
            return s_verbs.TryGetValue(NormalizeVerb(name), out spec);
        }

        /// <summary>归一化 verb 名：允许 `move` / `action.move` / `Move` 三种写法。</summary>
        public static string NormalizeVerb(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            string trimmed = name.Trim();
            int dot = trimmed.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < trimmed.Length)
                trimmed = trimmed.Substring(dot + 1);
            return trimmed.ToLowerInvariant();
        }

        private static void Register(ActionVerbSpec spec)
        {
            s_verbs[spec.Name] = spec;
        }

        private static void RegisterBuiltIns()
        {
            Register(new ActionVerbSpec("wait", "基础", "原地等待若干毫秒（不产生任何输入）",
                (args, resolver, out error) =>
                {
                    error = null;
                    int ms = RequireDuration(args, "ms", 500, out error);
                    if (error != null) return null;
                    return new ActionClip("wait").Wait(ms);
                },
                new ActionParamSpec("ms", ActionParamKind.DurationMs, true, "500", null, "等待时长（毫秒）")));

            Register(new ActionVerbSpec("move", "移动", "朝一个方向持续移动若干毫秒（八个方向 + 原地）",
                CompileMove, MoveParams()));

            Register(new ActionVerbSpec("turn", "视角", "转向绝对角度（度）或相对增量（度）",
                CompileTurn,
                new ActionParamSpec("yaw", ActionParamKind.Float, false, null, null, "绝对偏航角（度）"),
                new ActionParamSpec("pitch", ActionParamKind.Float, false, "0", null, "绝对俯仰角（度）"),
                new ActionParamSpec("dyaw", ActionParamKind.Float, false, null, null, "相对偏航增量（度）"),
                new ActionParamSpec("dpitch", ActionParamKind.Float, false, "0", null, "相对俯仰增量（度）"),
                new ActionParamSpec("ms", ActionParamKind.DurationMs, false, "100", null, "转向占用时长（毫秒）")));

            Register(new ActionVerbSpec("lookdir", "视角", "朝八个方向之一看（世界坐标近似；精确瞄准请用 lookAt）",
                CompileLookDir,
                new ActionParamSpec("dir", ActionParamKind.Direction, true, null, DirectionNames,
                    "方向：f/b/l/r = 北/南/西/东；fl/fr/bl/br = 斜向"),
                new ActionParamSpec("ms", ActionParamKind.DurationMs, false, "100", null, "转向占用时长（毫秒）")));

            Register(new ActionVerbSpec("lookAt", "视角", "看向语义目标（准星格 / 实体 / 显式坐标）",
                CompileLookAt,
                new ActionParamSpec("target", ActionParamKind.String, true, "aim", TargetNames,
                    "语义目标：aim / entity / self / x,y,z"),
                new ActionParamSpec("ms", ActionParamKind.DurationMs, false, "100", null, "转向占用时长（毫秒）")));

            Register(new ActionVerbSpec("hotbar", "物品", "选择快捷栏槽位（1..10）",
                (args, resolver, out error) =>
                {
                    error = null;
                    if (!args.Has("slot"))
                    {
                        error = ActionErrorCodes.InvalidArgument + ": 'slot' is required for hotbar";
                        return null;
                    }
                    int slot = args.GetInt("slot", -1);
                    if (slot < 1 || slot > 10)
                    {
                        error = ActionErrorCodes.InvalidArgument + ": slot must be 1..10, found " + slot;
                        return null;
                    }
                    var clip = new ActionClip("hotbar:" + slot);
                    clip.Add(new ActionFrame
                    {
                        DeltaMs = ActionCompileDefaults.PulseMs,
                        SelectSlot = slot,
                        KeysPressed = new[] { SlotKey(slot) }
                    });
                    return clip;
                },
                new ActionParamSpec("slot", ActionParamKind.Slot, true, null, null, "快捷栏槽位 1..10")));

            Register(new ActionVerbSpec("jump", "移动", "跳一下（按下即跳）",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("jump");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeyJump } });
                    return clip;
                }));

            Register(new ActionVerbSpec("sneak", "移动", "切换潜行（开关语义，按一次）",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("sneak");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeySneak } });
                    return clip;
                }));

            Register(new ActionVerbSpec("mount", "移动", "上/下坐骑（开关语义）",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("mount");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeyMount } });
                    return clip;
                }));

            Register(new ActionVerbSpec("fly", "移动", "切换创造飞行（开关语义）",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("fly");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeyCreativeFly } });
                    return clip;
                }));

            Register(new ActionVerbSpec("dig", "世界交互", "持续左键挖掘/作用（可先瞄向语义目标）",
                CompileDig,
                DigParams()));

            Register(new ActionVerbSpec("hit", "世界交互", "左键点击一次（击打）",
                CompileMouseStep("hit", "left", "click"),
                DigParams()));

            Register(new ActionVerbSpec("interact", "世界交互", "右键点击一次（交互/放置）",
                CompileMouseStep("interact", "right", "click"),
                DigParams()));

            Register(new ActionVerbSpec("attack", "世界交互", "看向目标并左键点击一次",
                CompileAttack, DigParams()));

            Register(new ActionVerbSpec("aim", "世界交互", "右键按住/松开（瞄准开关）",
                (args, resolver, out error) =>
                {
                    error = null;
                    bool on = args.GetBool("on", true);
                    var clip = new ActionClip("aim:" + (on ? "on" : "off"));
                    clip.Add(new ActionFrame
                    {
                        DeltaMs = ActionCompileDefaults.PulseMs,
                        MouseAction = on ? "down" : "up",
                        MouseButtonName = "right"
                    });
                    return clip;
                },
                new ActionParamSpec("on", ActionParamKind.Bool, false, "true", null, "true = 按住瞄准，false = 松开")));

            Register(new ActionVerbSpec("drop", "物品", "丢弃手持物（按一次丢弃键）",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("drop");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeyDrop } });
                    return clip;
                }));

            Register(new ActionVerbSpec("ui", "界面", "界面操作：点语义目标 / 打字 / 滚轮",
                CompileUi,
                new ActionParamSpec("click", ActionParamKind.String, false, null, null,
                    "UI 语义选择器（`Play` / `[Screen]/…/Play` / `list:WorldsList@世界名` / `list:WorldsList#0`）"),
                new ActionParamSpec("text", ActionParamKind.String, false, null, null, "要输入的文本（需焦点已在输入框）"),
                new ActionParamSpec("wheel", ActionParamKind.Int, false, "0", null, "滚轮格数（正 = 向上）"),
                new ActionParamSpec("key", ActionParamKind.String, false, null, null, "按一次某个键（例如 E 开背包、C 开衣物）")));

            Register(new ActionVerbSpec("sleep", "复合", "睡觉：开衣物面板 → 等按钮 → 点睡觉（复合 verb）",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("sleep");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeyClothing } });
                    clip.Wait(800);
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, UiClick = "SleepButton" });
                    return clip;
                }));

            Register(new ActionVerbSpec("openInventory", "复合", "打开/关闭背包",
                (args, resolver, out error) =>
                {
                    error = null;
                    var clip = new ActionClip("openInventory");
                    clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, KeysPressed = new[] { KeyInventory } });
                    clip.Wait(400);
                    return clip;
                }));

            Register(new ActionVerbSpec("eat", "复合", "吃：选槽位 → 瞄向空地 → 右键一次（复合 verb）",
                CompileEat,
                new ActionParamSpec("slot", ActionParamKind.Slot, false, null, null, "要吃的槽位 1..10；不给则由解析器给 `first_food`"),
                new ActionParamSpec("target", ActionParamKind.String, false, "first_food", TargetNames,
                    "食物来源语义目标：first_food（解析成槽位）/ 显式槽位")));
        }

        private static ActionParamSpec[] MoveParams()
        {
            return new[]
            {
                new ActionParamSpec("dir", ActionParamKind.Direction, true, "f", DirectionNames,
                    "方向：f/b/l/r = 前/后/左/右；fl/fr/bl/br = 斜向"),
                new ActionParamSpec("ms", ActionParamKind.DurationMs, true, "500", null, "持续时长（毫秒）"),
                new ActionParamSpec("jump", ActionParamKind.Bool, false, "false", null, "移动过程中是否跳跃"),
                new ActionParamSpec("sneak", ActionParamKind.Bool, false, "false", null, "移动过程中是否潜行（慢走）")
            };
        }

        private static ActionParamSpec[] DigParams()
        {
            return new[]
            {
                new ActionParamSpec("ms", ActionParamKind.DurationMs, false, "600", null, "持续时长（毫秒；hit/interact/attack 忽略它）"),
                new ActionParamSpec("target", ActionParamKind.String, false, null, TargetNames,
                    "先瞄向的语义目标（aim / entity / self / x,y,z）；不给则不动视角")
            };
        }

        public static readonly string[] DirectionNames =
        {
            "f", "b", "l", "r", "fl", "fr", "bl", "br", "none"
        };

        public static readonly string[] TargetNames =
        {
            "aim", "entity", "self", "first_food"
        };

        // ---------------------------------------------------------------- 编译：移动 / 视角

        private static ActionClip CompileMove(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            string dir = (args.GetString("dir", "f") ?? "f").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(dir))
                dir = "f";

            var keys = new List<string>();
            if (dir.IndexOf('f') >= 0) keys.Add(KeyForward);
            if (dir.IndexOf('b') >= 0) keys.Add(KeyBack);
            if (dir.IndexOf('l') >= 0) keys.Add(KeyLeft);
            if (dir.IndexOf('r') >= 0) keys.Add(KeyRight);
            if (dir != "none" && keys.Count == 0)
            {
                error = ActionErrorCodes.InvalidArgument + ": unknown move dir '" + dir
                    + "' (expected one of " + string.Join("|", DirectionNames) + ")";
                return null;
            }
            if (args.GetBool("sneak", false))
                keys.Add(KeySneak);

            int ms = RequireDuration(args, "ms", 500, out error);
            if (error != null)
                return null;

            var clip = new ActionClip("move:" + dir);
            if (args.GetBool("jump", false))
            {
                // 先跳一下，再按方向走（跳与走可以同帧，但分开更接近真人操作）
                clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.FrameMs, KeysPressed = new[] { KeyJump } });
            }
            if (dir == "none" || keys.Count == 0)
                clip.Wait(ms);
            else
                clip.Hold(ms, ActionCompileDefaults.FrameMs, keys.ToArray());
            return clip;
        }

        private static ActionClip CompileTurn(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            var clip = new ActionClip("turn");
            int ms = RequireDuration(args, "ms", ActionCompileDefaults.LookMs, out error);
            if (error != null)
                return null;

            if (args.Has("yaw"))
            {
                var frame = ActionFrame.Idle(ms);
                frame.HasAbsoluteLook = true;
                frame.YawDegrees = args.GetFloat("yaw", 0f);
                frame.PitchDegrees = args.GetFloat("pitch", 0f);
                clip.Add(frame);
                return clip;
            }

            if (args.Has("dyaw") || args.Has("dpitch"))
            {
                var frame = ActionFrame.Idle(ms);
                frame.LookDeltaYaw = DegreesToRadians(args.GetFloat("dyaw", 0f));
                frame.LookDeltaPitch = DegreesToRadians(args.GetFloat("dpitch", 0f));
                clip.Add(frame);
                return clip;
            }

            error = ActionErrorCodes.InvalidArgument + ": turn needs 'yaw' (+ 'pitch') or 'dyaw'/'dpitch'";
            return null;
        }

        private static ActionClip CompileLookDir(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            string dir = (args.GetString("dir", null) ?? string.Empty).Trim().ToLowerInvariant();
            float yaw;
            if (!TryDirectionYaw(dir, out yaw))
            {
                error = ActionErrorCodes.InvalidArgument + ": unknown look dir '" + dir + "'";
                return null;
            }

            int ms = RequireDuration(args, "ms", ActionCompileDefaults.LookMs, out error);
            if (error != null)
                return null;

            var frame = ActionFrame.Idle(ms);
            frame.HasAbsoluteLook = true;
            frame.YawDegrees = yaw;
            frame.PitchDegrees = 0f;
            return new ActionClip("lookdir:" + dir).Add(frame);
        }

        /// <summary>八个方向 → 绝对偏航角（度）。世界坐标近似，精确瞄准请用 lookAt。</summary>
        public static bool TryDirectionYaw(string dir, out float yawDegrees)
        {
            switch (dir)
            {
                case "f": yawDegrees = 0f; return true;
                case "b": yawDegrees = 180f; return true;
                case "l": yawDegrees = 90f; return true;
                case "r": yawDegrees = -90f; return true;
                case "fl": yawDegrees = 45f; return true;
                case "fr": yawDegrees = -45f; return true;
                case "bl": yawDegrees = 135f; return true;
                case "br": yawDegrees = -135f; return true;
                default: yawDegrees = 0f; return false;
            }
        }

        private static ActionClip CompileLookAt(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            string target = args.GetString("target", "aim");
            int ms = RequireDuration(args, "ms", ActionCompileDefaults.LookMs, out error);
            if (error != null)
                return null;

            var clip = new ActionClip("lookAt:" + target);
            ActionTarget resolved;
            if (!TryResolveTarget(target, resolver, out resolved, out error))
                return null;

            var frame = ActionFrame.Idle(ms);
            if (resolved != null && resolved.HasLookPoint)
            {
                frame.HasLookAt = true;
                frame.LookAtX = resolved.LookX;
                frame.LookAtY = resolved.LookY;
                frame.LookAtZ = resolved.LookZ;
            }
            clip.Add(frame);
            return clip;
        }

        // ---------------------------------------------------------------- 编译：世界交互

        private static ActionClip CompileDig(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            int ms = RequireDuration(args, "ms", 600, out error);
            if (error != null)
                return null;

            var clip = new ActionClip("dig");
            if (!TryAppendAim(args, resolver, clip, out error))
                return null;

            // 左键按住 = 挖/持续作用（Source: cmd-bridge-plan.md §2.5）
            var frame = ActionFrame.Idle(ms);
            frame.MouseAction = "down";
            frame.MouseButtonName = "left";
            clip.Add(frame);

            var release = ActionFrame.Idle(ActionCompileDefaults.FrameMs);
            release.MouseAction = "up";
            release.MouseButtonName = "left";
            clip.Add(release);
            return clip;
        }

        private static ActionFunc CompileMouseStep(string name, string button, string action)
        {
            return (ActionArgs args, IActionTargetResolver resolver, out string error) =>
            {
                error = null;
                var clip = new ActionClip(name);
                if (!TryAppendAim(args, resolver, clip, out error))
                    return null;

                var frame = ActionFrame.Idle(ActionCompileDefaults.PulseMs);
                frame.MouseAction = action;
                frame.MouseButtonName = button;
                clip.Add(frame);

                if (string.Equals(action, "click", StringComparison.OrdinalIgnoreCase))
                {
                    // 合成点击（down+up）在引擎里派生 Tap/Click，不需要额外两帧
                    var release = ActionFrame.Idle(ActionCompileDefaults.FrameMs);
                    release.MouseAction = "up";
                    release.MouseButtonName = button;
                    clip.Add(release);
                }
                return clip;
            };
        }

        private static ActionClip CompileAttack(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            var clip = new ActionClip("attack");
            if (!TryAppendAim(args, resolver, clip, out error))
                return null;

            clip.Add(new ActionFrame
            {
                DeltaMs = ActionCompileDefaults.PulseMs,
                MouseAction = "click",
                MouseButtonName = "left"
            });
            clip.Add(new ActionFrame
            {
                DeltaMs = ActionCompileDefaults.FrameMs,
                MouseAction = "up",
                MouseButtonName = "left"
            });
            return clip;
        }

        /// <summary>把 `target=` 编译成"先看向目标"的前导帧。</summary>
        private static bool TryAppendAim(ActionArgs args, IActionTargetResolver resolver, ActionClip clip,
            out string error)
        {
            error = null;
            string target = args.GetString("target", null);
            if (string.IsNullOrEmpty(target))
                return true;

            ActionTarget resolved;
            if (!TryResolveTarget(target, resolver, out resolved, out error))
                return false;

            if (resolved == null || !resolved.HasLookPoint)
                return true;

            var frame = ActionFrame.Idle(ActionCompileDefaults.LookMs);
            frame.HasLookAt = true;
            frame.LookAtX = resolved.LookX;
            frame.LookAtY = resolved.LookY;
            frame.LookAtZ = resolved.LookZ;
            clip.Add(frame);
            return true;
        }

        // ---------------------------------------------------------------- 编译：界面与复合

        private static ActionClip CompileUi(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            var clip = new ActionClip("ui");
            bool any = false;

            string click = args.GetString("click", null);
            if (!string.IsNullOrEmpty(click))
            {
                var frame = ActionFrame.Idle(ActionCompileDefaults.PulseMs);
                frame.UiClick = click;
                clip.Add(frame);
                any = true;
            }

            string key = args.GetString("key", null);
            if (!string.IsNullOrEmpty(key))
            {
                clip.Add(new ActionFrame
                {
                    DeltaMs = ActionCompileDefaults.PulseMs,
                    KeysPressed = new[] { key.Trim() }
                });
                any = true;
            }

            string text = args.GetString("text", null);
            if (!string.IsNullOrEmpty(text))
            {
                // 打字要先把焦点给输入框；顺序由脚本作者用 `ui click=...` 保证
                clip.Add(new ActionFrame { DeltaMs = ActionCompileDefaults.PulseMs, TypeText = text });
                any = true;
            }

            int wheel = args.GetInt("wheel", 0);
            if (wheel != 0)
            {
                var frame = ActionFrame.Idle(ActionCompileDefaults.PulseMs);
                frame.Wheel = wheel;
                clip.Add(frame);
                any = true;
            }

            if (!any)
            {
                error = ActionErrorCodes.InvalidArgument
                    + ": ui needs at least one of click= / key= / text= / wheel=";
                return null;
            }
            return clip;
        }

        private static ActionClip CompileEat(ActionArgs args, IActionTargetResolver resolver, out string error)
        {
            error = null;
            var clip = new ActionClip("eat");

            int slot = args.GetInt("slot", -1);
            if (slot < 0)
            {
                // §4.9：模型给枚举（first_food），C# 解析成具体槽位
                ActionTarget target;
                if (!TryResolveTarget(args.GetString("target", "first_food"), resolver, out target, out error))
                    return null;
                slot = target != null ? target.X : -1; // first_food 用 X 回传槽位
            }

            if (slot >= 1 && slot <= 10)
            {
                clip.Add(new ActionFrame
                {
                    DeltaMs = ActionCompileDefaults.PulseMs,
                    SelectSlot = slot,
                    KeysPressed = new[] { SlotKey(slot) }
                });
                clip.Wait(250);
            }

            // 吃东西 = 瞄向空地 + 右键一次（没有专用快捷键，走正常物品流程）
            clip.Add(new ActionFrame
            {
                DeltaMs = ActionCompileDefaults.PulseMs,
                MouseAction = "click",
                MouseButtonName = "right"
            });
            clip.Add(new ActionFrame
            {
                DeltaMs = ActionCompileDefaults.FrameMs,
                MouseAction = "up",
                MouseButtonName = "right"
            });
            return clip;
        }

        // ---------------------------------------------------------------- 参数读取与校验

        private static int RequireDuration(ActionArgs args, string name, int fallbackMs, out string error)
        {
            error = null;
            float value = args.GetFloat(name, fallbackMs);
            if (value < 0f)
            {
                error = ActionErrorCodes.InvalidArgument + ": '" + name + "' must be >= 0, found "
                    + value.ToString("0.##", CultureInfo.InvariantCulture);
                return 0;
            }
            int ms = (int)Math.Round(value);
            if (ms > ActionCompileDefaults.MaxTotalMs)
            {
                error = ActionErrorCodes.InvalidArgument + ": '" + name + "' exceeds the per-step limit ("
                    + ActionCompileDefaults.MaxTotalMs + " ms)";
                return 0;
            }
            return ms;
        }

        private static bool TryResolveTarget(string target, IActionTargetResolver resolver,
            out ActionTarget resolved, out string error)
        {
            resolved = null;
            error = null;
            if (string.IsNullOrEmpty(target))
                return true;

            // 显式坐标：`x,y,z`
            if (target.IndexOf(',') > 0)
            {
                string[] parts = target.Split(',');
                float x, y, z;
                if (parts.Length == 3
                    && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)
                    && float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                {
                    resolved = ActionTarget.Point(x, y, z);
                    return true;
                }
                error = ActionErrorCodes.InvalidArgument + ": malformed coordinate target '" + target + "'";
                return false;
            }

            if (resolver == null)
            {
                // 没有解析器时，"语义目标"是硬错误：宁可失败，也不要对着空气发动作（§4.9）
                error = ActionErrorCodes.StepFailed + ": no target resolver is available for '" + target + "'";
                return false;
            }

            resolved = resolver.Resolve(target, out error);
            return error == null;
        }

        // ---------------------------------------------------------------- 整步入口

        /// <summary>
        /// 编译一步（verb + 参数）。返回 null 时 <paramref name="error"/> 带稳定错误码前缀。
        /// </summary>
        public static ActionClip CompileStep(string verb, ActionArgs args, IActionTargetResolver resolver,
            out string error)
        {
            error = null;
            ActionVerbSpec spec;
            if (!TryGetVerb(verb, out spec))
            {
                error = ActionErrorCodes.VerbUnknown + ": unknown verb '" + verb + "' (known: "
                    + string.Join(", ", VerbNames) + ")";
                return null;
            }

            args = args ?? new ActionArgs();
            List<string> unknown = args.UnknownNames(spec.Parameters);
            if (unknown.Count > 0)
            {
                error = ActionErrorCodes.InvalidArgument + ": unknown parameter(s) for '" + spec.Name + "': "
                    + string.Join(", ", unknown);
                return null;
            }

            for (int i = 0; i < spec.Parameters.Count; i++)
            {
                ActionParamSpec param = spec.Parameters[i];
                if (!param.Required || args.Has(param.Name))
                    continue;
                error = ActionErrorCodes.InvalidArgument + ": '" + param.Name + "' is required for "
                    + spec.Name;
                return null;
            }

            ActionClip clip = spec.Compile(args, resolver, out error);
            if (clip == null)
            {
                if (string.IsNullOrEmpty(error))
                    error = ActionErrorCodes.VerbFailed + ": verb '" + spec.Name + "' produced no clip";
                return null;
            }
            return clip;
        }

        private static float DegreesToRadians(float degrees)
        {
            return degrees * (float)Math.PI / 180f;
        }

        /// <summary>`wait` 用到的毫秒上限，暴露给测试。</summary>
        public static int MaxStepMilliseconds
        {
            get { return ActionCompileDefaults.MaxTotalMs; }
        }
    }

    /// <summary>verb 的编译委托。</summary>
    public delegate ActionClip ActionFunc(ActionArgs args, IActionTargetResolver resolver, out string error);

    /// <summary>
    /// 一个 verb 的注册信息：名字 / 分类 / 说明 / 参数表 / 编译器。
    /// 与行为树节点的 <see cref="BtNodeInfo"/> 同构（形状 → 参数表 → 工厂）。
    /// </summary>
    public sealed class ActionVerbSpec
    {
        public ActionVerbSpec(string name, string category, string description, ActionFunc compile,
            params ActionParamSpec[] parameters)
        {
            Name = name;
            Category = category;
            Description = description;
            Compile = compile;
            Parameters = parameters ?? new ActionParamSpec[0];
        }

        public string Name { get; }

        public string Category { get; }

        public string Description { get; }

        public ActionFunc Compile { get; }

        public IReadOnlyList<ActionParamSpec> Parameters { get; }

        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            text.Append(Name).Append(" — ").Append(Description);
            for (int i = 0; i < Parameters.Count; i++)
                text.Append("\n    ").Append(Parameters[i].Describe());
            return text.ToString();
        }

        public override string ToString()
        {
            return Name + "(" + Parameters.Count + " params)";
        }
    }
}

using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 玩家状态 + "玩家当前在做什么"（ComponentInput.PlayerInput）。
    ///
    /// 这是 AI 相对视觉的核心信息优势：无需截图即可知道按键意图、准星、生命与背包。
    /// 全程只读；本文件不得出现任何写操作。
    /// </summary>
    internal static class PlayerObserver
    {
        public static Dictionary<string, object> Describe()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            result["loaded"] = false;

            if (GameManager.Project == null)
                return result;

            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            if (players == null || players.ComponentPlayers.Count == 0)
                return result;

            ComponentPlayer player = players.ComponentPlayers[0];
            result["loaded"] = true;
            result["playerCount"] = players.ComponentPlayers.Count;

            ComponentBody body = player.ComponentBody;
            if (body != null)
            {
                result["position"] = Vector3ToDictionary(body.Position);
                result["velocity"] = Vector3ToDictionary(body.Velocity);
            }

            result["look"] = DescribeLook(player);
            result["health"] = DescribeHealth(player);
            result["vitals"] = DescribeVitals(player);
            result["inventory"] = DescribeInventory(player);
            result["input"] = DescribeInput(player);
            result["sleep"] = DescribeSleep(player);
            result["hud"] = DescribeHud(player);
            result["gameMode"] = DescribeGameMode();
            try
            {
                // Source: Survivalcraft/Game/PlayerData.cs:118
                result["level"] = player.PlayerData.Level;
            }
            catch
            {
                result["level"] = null;
            }

            return result;
        }

        public static Dictionary<string, object> DescribeLook(ComponentPlayer player)
        {
            var look = new Dictionary<string, object>(StringComparer.Ordinal);
            float radiansToDegrees = 180f / MathUtils.PI;

            // Source: Survivalcraft/Game/ComponentLocomotion.cs:303（游戏自身的 yaw 提取公式）
            Quaternion rotation = player.ComponentBody.Rotation;
            float yaw = MathUtils.Atan2(
                2f * rotation.Y * rotation.W - 2f * rotation.X * rotation.Z,
                1f - 2f * rotation.Y * rotation.Y - 2f * rotation.Z * rotation.Z);
            look["yawDeg"] = yaw * radiansToDegrees;

            try
            {
                Vector2 angles = ModManager.Instance.ModParentField.GetParentField<Vector2>(
                    player.ComponentLocomotion,
                    InputWhitelist.LocomotionLookAngles,
                    typeof(ComponentLocomotion));
                look["pitchDeg"] = angles.Y * radiansToDegrees;
                look["headYawDeg"] = angles.X * radiansToDegrees;
            }
            catch
            {
                look["pitchDeg"] = null;
            }

            return look;
        }

        /// <summary>
        /// 原始输入读数：直接读引擎的键盘/鼠标状态数组，不依赖世界是否加载。
        /// 用途：验证注入是否真的落到引擎输入层（"AI 控制玩家控制器"的取证通道）。
        /// </summary>
        public static Dictionary<string, object> DescribeRawInput()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                IModParentField fields = ModManager.Instance.ModParentField;

                bool[] keysDown = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownArray);
                bool[] keysOnce = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownOnceArray);
                var down = new List<string>();
                var once = new List<string>();
                if (keysDown != null)
                {
                    for (int i = 0; i < keysDown.Length; i++)
                    {
                        if (keysDown[i])
                            down.Add(((Key)i).ToString());
                    }
                }
                if (keysOnce != null)
                {
                    for (int i = 0; i < keysOnce.Length; i++)
                    {
                        if (keysOnce[i])
                            once.Add(((Key)i).ToString());
                    }
                }
                result["keysDown"] = down;
                result["keysDownOnce"] = once;
                result["lastKey"] = Keyboard.LastKey.HasValue
                    ? Keyboard.LastKey.Value.ToString()
                    : null;
                result["lastChar"] = Keyboard.LastChar.HasValue
                    ? Keyboard.LastChar.Value.ToString()
                    : null;

                bool[] mouseDown = fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownArray);
                var buttons = new List<string>();
                if (mouseDown != null)
                {
                    for (int i = 0; i < mouseDown.Length; i++)
                    {
                        if (mouseDown[i])
                            buttons.Add(((MouseButton)i).ToString());
                    }
                }
                result["mouseButtonsDown"] = buttons;
                result["mouseWheelMovement"] = Mouse.MouseWheelMovement;

                // 根控件树的 WidgetInput 派生状态：用于诊断"点击残留"。
                // 正常情况下 Press/Tap/Click 只在输入发生的那一帧存在；转屏动画期间控件树停止
                // Update（ScreensManager.cs:81），这些字段会冻结，是历史上一次点击被重复上报的根因。
                try
                {
                    ContainerWidget root = ScreensManager.RootWidget;
                    WidgetInput rootInput = root != null ? root.WidgetsHierarchyInput : null;
                    if (rootInput != null)
                    {
                        IModParentField rootFields = ModManager.Instance.ModParentField;
                        result["rootInput"] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["useSoftMouseCursor"] = rootInput.UseSoftMouseCursor,
                            ["hasPress"] = rootFields.GetParentField(rootInput, "Press") != null,
                            ["hasTap"] = rootFields.GetParentField(rootInput, "Tap") != null,
                            ["hasClick"] = rootFields.GetParentField(rootInput, "Click") != null,
                            ["hasSpecialClick"] = rootFields.GetParentField(rootInput, "SpecialClick") != null,
                            ["hasDrag"] = rootFields.GetParentField(rootInput, "Drag") != null,
                            ["hasScroll"] = rootFields.GetParentField(rootInput, "Scroll") != null,
                            ["mouseDownPointRemaining"] =
                                rootFields.GetParentField(rootInput, "m_mouseDownPoint") != null
                        };
                    }
                }
                catch (Exception rootInputException)
                {
                    result["rootInputError"] = rootInputException.Message;
                }
                result["mousePosition"] = Mouse.MousePosition.HasValue
                    ? new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["x"] = Mouse.MousePosition.Value.X,
                        ["y"] = Mouse.MousePosition.Value.Y
                    }
                    : null;
            }
            catch (Exception exception)
            {
                result["error"] = exception.GetType().Name + ": " + exception.Message;
            }
            return result;
        }

        /// <summary>
        /// 睡觉状态。这是 AI 必须知道的两件事：
        ///   1. "能不能手动睡" —— 由游戏自身的 CanSleep 判定（脚下方块 SleepSuitability 必须 &gt; 0、
        ///      需要遮蔽、不能太湿、得够困）。石块/石板类方块 SleepSuitability = 0，手动睡会被拒绝。
        ///   2. "是不是已经昏睡了" —— 困到 Sleep 归零时 ComponentVitalStats 会直接调用
        ///      Sleep(allowManualWakeup: false)，**绕过 CanSleep**，于是可以睡在不该睡的地方。
        /// Source: Survivalcraft/Game/ComponentSleep.cs:41-79（CanSleep）
        /// Source: Survivalcraft/Game/ComponentVitalStats.cs:431-478（UpdateSleep / 昏睡）
        /// </summary>
        private static Dictionary<string, object> DescribeSleep(ComponentPlayer player)
        {
            var sleep = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                ComponentSleep component = player.Entity.FindComponent<ComponentSleep>(false);
                if (component == null)
                    return null;

                sleep["isSleeping"] = component.IsSleeping;
                sleep["sleepFactor"] = component.SleepFactor;

                string reason = null;
                bool canSleep = false;
                try
                {
                    canSleep = component.CanSleep(out reason);
                }
                catch (Exception exception)
                {
                    reason = exception.GetType().Name + ": " + exception.Message;
                }
                sleep["canSleep"] = canSleep;
                sleep["canSleepReason"] = string.IsNullOrEmpty(reason) ? null : reason;

                try
                {
                    // ScMultiplayer 也这样只读该字段：区分"主动入睡"与"困到昏睡"。
                    sleep["allowManualWakeUp"] = ModManager.Instance.ModParentField
                        .GetParentField<bool>(component, "m_allowManualWakeUp", typeof(ComponentSleep));
                }
                catch
                {
                    sleep["allowManualWakeUp"] = null;
                }

                ComponentBody body = player.ComponentBody;
                if (body != null && body.StandingOnValue.HasValue)
                {
                    int contents = Terrain.ExtractContents(body.StandingOnValue.Value);
                    var block = BlocksManager.Blocks[contents];
                    sleep["standingOn"] = block != null ? block.GetType().Name : null;
                    sleep["standingOnSleepSuitability"] = block != null ? block.SleepSuitability : 0f;
                }
                else
                {
                    sleep["standingOn"] = null;
                    sleep["standingOnSleepSuitability"] = null;
                }

                try
                {
                    // Sleep 每秒衰减 1/1800（ComponentVitalStats.cs:444），据此给出"还有多久会昏睡"。
                    ComponentVitalStats vitals = player.ComponentVitalStats;
                    if (vitals != null && !component.IsSleeping)
                        sleep["secondsUntilFaint"] = MathUtils.Max(0f, vitals.Sleep * 1800f);
                }
                catch
                {
                }
            }
            catch
            {
                return null;
            }
            return sleep;
        }

        private static Dictionary<string, object> DescribeHealth(ComponentPlayer player)
        {
            var health = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                ComponentHealth component = player.ComponentHealth;
                if (component == null)
                    return null;
                health["health"] = component.Health;
                health["air"] = component.Air;
            }
            catch
            {
                return null;
            }
            return health;
        }

        private static Dictionary<string, object> DescribeVitals(ComponentPlayer player)
        {
            try
            {
                ComponentVitalStats vitals = player.ComponentVitalStats;
                if (vitals == null)
                    return null;
                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["food"] = vitals.Food,
                    ["stamina"] = vitals.Stamina,
                    ["sleep"] = vitals.Sleep,
                    ["temperature"] = vitals.Temperature,
                    ["wetness"] = vitals.Wetness
                };
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> DescribeInventory(ComponentPlayer player)
        {
            try
            {
                IInventory inventory = player.ComponentMiner != null
                    ? player.ComponentMiner.Inventory
                    : null;
                if (inventory == null)
                    return null;

                int activeSlot = inventory.ActiveSlotIndex;
                int value = inventory.GetSlotValue(activeSlot);
                var slots = new List<Dictionary<string, object>>();
                int count = Math.Min(inventory.SlotsCount, 16);
                for (int i = 0; i < count; i++)
                {
                    int slotValue = inventory.GetSlotValue(i);
                    int slotCount = inventory.GetSlotCount(i);
                    if (slotValue == 0 && slotCount == 0)
                        continue;
                    slots.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["slot"] = i,
                        ["value"] = slotValue,
                        ["contents"] = Terrain.ExtractContents(slotValue),
                        ["count"] = slotCount
                    });
                }

                return new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["slotsCount"] = inventory.SlotsCount,
                    ["activeSlot"] = activeSlot,
                    ["holding"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["value"] = value,
                        ["contents"] = Terrain.ExtractContents(value),
                        ["count"] = inventory.GetSlotCount(activeSlot)
                    },
                    ["slots"] = slots
                };
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, object> DescribeInput(ComponentPlayer player)
        {
            var input = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                PlayerInput playerInput = player.ComponentInput.PlayerInput;
                input["move"] = Vector3ToDictionary(playerInput.Move);
                input["look"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["x"] = playerInput.Look.X,
                    ["y"] = playerInput.Look.Y
                };
                input["jump"] = playerInput.Jump;
                input["dig"] = playerInput.Dig.HasValue;
                input["hit"] = playerInput.Hit.HasValue;
                input["aim"] = playerInput.Aim.HasValue;
                input["interact"] = playerInput.Interact.HasValue;
                input["drop"] = playerInput.Drop;
                input["selectSlot"] = playerInput.SelectInventorySlot;
                input["scrollInventory"] = playerInput.ScrollInventory;
                input["toggleInventory"] = playerInput.ToggleInventory;
                input["toggleCrouch"] = playerInput.ToggleCrouch;
                input["toggleMount"] = playerInput.ToggleMount;
                input["toggleCreativeFly"] = playerInput.ToggleCreativeFly;
            }
            catch
            {
            }

            var heldKeys = new List<string>();
            try
            {
                bool[] keysDown = ModManager.Instance.ModParentField.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownArray);
                if (keysDown != null)
                {
                    for (int i = 0; i < keysDown.Length; i++)
                    {
                        if (keysDown[i])
                            heldKeys.Add(((Key)i).ToString());
                    }
                }
            }
            catch
            {
            }
            input["keysDown"] = heldKeys;
            return input;
        }

        private static Dictionary<string, object> DescribeHud(ComponentPlayer player)
        {
            var hud = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                ComponentGui gui = player.ComponentGui;
                hud["controlsVisible"] = gui != null && gui.ControlsContainerWidget != null
                    ? (bool?)gui.ControlsContainerWidget.IsVisibleGlobal
                    : null;
                hud["modalPanel"] = gui != null && gui.ModalPanelWidget != null
                    ? gui.ModalPanelWidget.GetType().Name
                    : null;
            }
            catch
            {
                hud["controlsVisible"] = null;
                hud["modalPanel"] = null;
            }

            try
            {
                hud["inputDevice"] = player.PlayerData.InputDevice.ToString();
                hud["readyForPlaying"] = player.PlayerData.IsReadyForPlaying;
            }
            catch
            {
            }

            hud["inputChannelsGatedByUiVisibility"] = false;
            hud["note"] = "Keyboard, mouse and look injection work even when the HUD is hidden; " +
                "only UI element clicks require the element to exist and be hittable.";
            return hud;
        }

        private static string DescribeGameMode()
        {
            try
            {
                SubsystemGameInfo info = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
                return info != null ? info.WorldSettings.GameMode.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        internal static Dictionary<string, object> Vector3ToDictionary(Vector3 value)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["x"] = value.X,
                ["y"] = value.Y,
                ["z"] = value.Z
            };
        }
    }
}

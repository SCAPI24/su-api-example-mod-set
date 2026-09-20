using Engine;
using Engine.Input;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 某一帧"玩家控制器看到的输入"。这是**只读快照**，写它不会改变游戏。
    ///
    /// 用途：动作包录制。计划 §6.2 要求"采样点选在帧末：此时 PlayerInput 已是人在这一帧的意图"，
    /// 所以录制端每帧读一次它即可 —— 而读原始字段的活儿仍然只在本 Mod 里做一次
    /// （白名单纪律：所有输入字段访问都经过 <see cref="InputWhitelist"/>，审计脚本才守得住）。
    /// </summary>
    public sealed class CmdBridgeInputFrame
    {
        public float MoveX;
        public float MoveY;
        public float MoveZ;

        /// <summary>本帧视角增量（弧度）—— 与引擎给 lookAngles 加的量同单位。</summary>
        public float LookX;
        public float LookY;

        public bool Jump;
        public bool Dig;
        public bool Hit;
        public bool Aim;
        public bool Interact;
        public bool Drop;
        public bool ToggleInventory;
        public bool ToggleCrouch;
        public bool ToggleMount;
        public bool ToggleCreativeFly;

        /// <summary>选中的快捷栏槽位；-1 = 本帧没选。</summary>
        public int SelectSlot = -1;

        public float ScrollInventory;

        /// <summary>本帧按住的键（键名）。</summary>
        public readonly List<string> KeysHeld = new List<string>();

        /// <summary>本帧刚按下的键（引擎 downOnce 语义：开背包/切潜行这类开关靠它）。</summary>
        public readonly List<string> KeysPressed = new List<string>();

        public bool MouseLeft;
        public bool MouseRight;
        public bool MouseMiddle;

        /// <summary>本帧滚轮格数。</summary>
        public int Wheel;

        /// <summary>
        /// 本帧做过的 UI 动作（`click:&lt;选择器或坐标&gt;`）。菜单/背包这类操作**没有对应的原始输入**
        /// —— 它们走的是引擎软光标（CM-1），所以录制成语义事件、回放时按时间点重放同一套点击。
        /// </summary>
        public readonly List<string> UiActions = new List<string>();

        /// <summary>读不到玩家输入时的原因（录制端据此跳过这一帧）。</summary>
        public string Error;
    }

    /// <summary>输入快照读取器（只读；任何失败都返回带 <see cref="CmdBridgeInputFrame.Error"/> 的帧）。</summary>
    internal static class InputSnapshot
    {
        /// <summary>读当前第一个玩家的输入（没有玩家时也返回一帧：只带原始输入层与 UI 动作）。</summary>
        public static CmdBridgeInputFrame ReadCurrent()
        {
            return Read(InputInjector.TryGetPlayer());
        }

        /// <summary>
        /// 读当前帧的玩家输入。<paramref name="player"/> 为 null（主菜单/世界未加载）时**照样返回一帧**：
        /// 此时没有角色可读，但原始键/鼠标/UI 动作仍然存在 —— 录制"进入游戏"这类纯 UI 流程全靠它。
        /// 派生字段（Move/Look/Jump…）在没有玩家时保持默认值，并在 <see cref="CmdBridgeInputFrame.Error"/>
        /// 里注明，调用方据此决定要不要用这些字段。
        /// </summary>
        public static CmdBridgeInputFrame Read(ComponentPlayer player)
        {
            var frame = new CmdBridgeInputFrame();
            if (player == null)
            {
                frame.Error = "no player yet (menus / world not loaded) - raw input only";
            }
            else
            {
                try
                {
                    PlayerInput input = player.ComponentInput.PlayerInput;
                    frame.MoveX = input.Move.X;
                    frame.MoveY = input.Move.Y;
                    frame.MoveZ = input.Move.Z;
                    frame.LookX = input.Look.X;
                    frame.LookY = input.Look.Y;
                    frame.Jump = input.Jump;
                    frame.Dig = input.Dig.HasValue;
                    frame.Hit = input.Hit.HasValue;
                    frame.Aim = input.Aim.HasValue;
                    frame.Interact = input.Interact.HasValue;
                    frame.Drop = input.Drop;
                    frame.ToggleInventory = input.ToggleInventory;
                    frame.ToggleCrouch = input.ToggleCrouch;
                    frame.ToggleMount = input.ToggleMount;
                    frame.ToggleCreativeFly = input.ToggleCreativeFly;
                    frame.SelectSlot = input.SelectInventorySlot.HasValue
                        ? input.SelectInventorySlot.Value : -1;
                    frame.ScrollInventory = input.ScrollInventory;
                }
                catch (Exception exception)
                {
                    frame.Error = "player input unavailable: " + exception.GetType().Name + ": "
                        + exception.Message;
                }
            }

            try
            {
                IModParentField fields = ModManager.Instance.ModParentField;

                // 原始键：按住 / 刚按下（回放走的就是这两个语义）
                bool[] keysDown = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownArray);
                if (keysDown != null)
                {
                    for (int i = 0; i < keysDown.Length; i++)
                    {
                        if (keysDown[i])
                            frame.KeysHeld.Add(((Key)i).ToString());
                    }
                }

                bool[] keysOnce = fields.GetStaticField<bool[]>(
                    typeof(Keyboard), InputWhitelist.KeyboardDownOnceArray);
                if (keysOnce != null)
                {
                    for (int i = 0; i < keysOnce.Length; i++)
                    {
                        if (keysOnce[i])
                            frame.KeysPressed.Add(((Key)i).ToString());
                    }
                }

                // 鼠标键：按下类（回放按位还原按下/松开，不解释成 Dig/Hit）
                bool[] mouseButtons = fields.GetStaticField<bool[]>(
                    typeof(Mouse), InputWhitelist.MouseDownArray);
                if (mouseButtons != null)
                {
                    frame.MouseLeft = mouseButtons.Length > (int)MouseButton.Left
                        && mouseButtons[(int)MouseButton.Left];
                    frame.MouseRight = mouseButtons.Length > (int)MouseButton.Right
                        && mouseButtons[(int)MouseButton.Right];
                    frame.MouseMiddle = mouseButtons.Length > (int)MouseButton.Middle
                        && mouseButtons[(int)MouseButton.Middle];
                }

                object wheel = fields.GetStaticField(
                    typeof(Mouse), InputWhitelist.MouseLastWheelValue);
                if (wheel is int)
                    frame.Wheel = (int)wheel;
            }
            catch (Exception exception)
            {
                if (string.IsNullOrEmpty(frame.Error))
                    frame.Error = "raw input unavailable: " + exception.GetType().Name + ": "
                        + exception.Message;
            }

            // UI 动作（软光标点击/拖拽）：由注入器记下、在这里**取走**（每帧读一次快照）
            try
            {
                List<string> actions = InputInjector.DrainUiActions();
                if (actions != null && actions.Count > 0)
                    frame.UiActions.AddRange(actions);
            }
            catch (Exception)
            {
            }

            return frame;
        }
    }
}

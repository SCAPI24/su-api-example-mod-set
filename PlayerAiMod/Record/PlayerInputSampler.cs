using CmdBridgeMod;
using Engine;
using Game;
using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 帧末采样器（P1，计划 §6.2）：把"人在这一帧干了什么"变成一条 <see cref="RecordingFrame"/>。
    ///
    /// 采样点选在**帧末**：此时 `ComponentInput.PlayerInput` 已经是这一帧的完整意图
    /// （移动合成、视角增量、鼠标键→Dig/Hit、快捷栏切换都已经算好），
    /// 读它比读原始设备状态更干净 —— 这也是计划里写明的位置。
    ///
    /// 输入快照本身由 CmdBridgeMod 提供（<see cref="CmdBridgeInput.ReadInputFrame"/>）：
    /// 原始键/鼠标数组的字段访问只在那一个 Mod 里做，白名单纪律与审计脚本才守得住。
    /// </summary>
    public static class PlayerInputSampler
    {
        /// <summary>
        /// 采一帧。返回 false 表示这一帧没采到（没有玩家/执行器不可用），录制会跳过后继续。
        /// </summary>
        public static bool TryCaptureFrame(AiRecordingSession session, IAiSensor sensors,
            ref CmdBridgeInputFrame previous, out string error)
        {
            error = null;
            if (session == null || session.Status.Phase != RecordingPhase.Recording)
                return false;

            CmdBridgeInputFrame current = null;
            try
            {
                CmdBridgeInput facade = CmdBridgeActuator.FacadeOrNull;
                current = facade != null ? facade.ReadInputFrame() : null;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }

            if (current == null)
            {
                error = "no player input is available right now";
                return false;
            }

            RecordingFrame frame = ToRecordingFrame(previous, current);
            session.CaptureFrame(frame, current.KeysHeld, current.KeysPressed);

            // 稀疏语义事件：快捷栏切换、鼠标按下、开关切换 —— 回放不依赖它们，但人看包时一眼能懂
            if (previous != null)
                CaptureEvents(session, previous, current);

            // 关键帧：位置/朝向（漂移检查的基准）
            if (sensors != null && sensors.IsReady)
            {
                float pitch;
                sensors.TryGetPitch(out pitch);
                session.CaptureKeyframe(session.Status.Duration, sensors.Position.X,
                    sensors.Position.Y, sensors.Position.Z,
                    sensors.YawRadians * 180f / MathUtils.PI, pitch * 180f / MathUtils.PI);
            }

            previous = current;
            return true;
        }

        /// <summary>把两帧快照之间的差异记成语义事件。</summary>
        private static void CaptureEvents(AiRecordingSession session, CmdBridgeInputFrame previous,
            CmdBridgeInputFrame current)
        {
            // UI 动作（菜单/背包点击）：**没有原始输入痕迹**，只能靠它自己的这条通道录下来。
            // 回放时 `ScatPlayer` 会在同样的时间点重放同一套点击（`ui.click` 事件）。
            if (current.UiActions != null)
            {
                for (int i = 0; i < current.UiActions.Count; i++)
                    session.CaptureEvent(session.Status.Duration, "ui.click", current.UiActions[i]);
            }

            if (previous.SelectSlot != current.SelectSlot && current.SelectSlot >= 0)
                session.CaptureEvent(session.Status.Duration, "selectSlot",
                    current.SelectSlot.ToString());

            if (!previous.MouseLeft && current.MouseLeft)
                session.CaptureEvent(session.Status.Duration, "mouseDown", "left");
            if (previous.MouseLeft && !current.MouseLeft)
                session.CaptureEvent(session.Status.Duration, "mouseUp", "left");
            if (!previous.MouseRight && current.MouseRight)
                session.CaptureEvent(session.Status.Duration, "mouseDown", "right");
            if (previous.MouseRight && !current.MouseRight)
                session.CaptureEvent(session.Status.Duration, "mouseUp", "right");

            if (!previous.ToggleInventory && current.ToggleInventory)
                session.CaptureEvent(session.Status.Duration, "toggle", "inventory");
            if (!previous.ToggleCrouch && current.ToggleCrouch)
                session.CaptureEvent(session.Status.Duration, "toggle", "crouch");
            if (!previous.ToggleMount && current.ToggleMount)
                session.CaptureEvent(session.Status.Duration, "toggle", "mount");
            if (!previous.ToggleCreativeFly && current.ToggleCreativeFly)
                session.CaptureEvent(session.Status.Duration, "toggle", "creativeFly");
            if (current.Wheel != 0)
                session.CaptureEvent(session.Status.Duration, "wheel", current.Wheel.ToString());
        }

        /// <summary>把 CmdBridgeMod 的输入快照转成录制帧（纯映射，键名在会话里登记成索引）。</summary>
        public static RecordingFrame ToRecordingFrame(CmdBridgeInputFrame previous,
            CmdBridgeInputFrame current)
        {
            var frame = new RecordingFrame();
            if (current == null)
                return frame;

            frame.MoveX = current.MoveX;
            frame.MoveY = current.MoveY;
            frame.MoveZ = current.MoveZ;
            frame.LookDeltaX = current.LookX;
            frame.LookDeltaY = current.LookY;
            frame.Jump = current.Jump;
            frame.Dig = current.Dig;
            frame.Hit = current.Hit;
            frame.Aim = current.Aim;
            frame.Interact = current.Interact;
            frame.Drop = current.Drop;
            frame.ToggleInventory = current.ToggleInventory;
            frame.ToggleCrouch = current.ToggleCrouch;
            frame.ToggleMount = current.ToggleMount;
            frame.ToggleCreativeFly = current.ToggleCreativeFly;
            frame.SelectSlot = current.SelectSlot;

            byte mouse = 0;
            if (current.MouseLeft) mouse |= 0x1;
            if (current.MouseRight) mouse |= 0x2;
            if (current.MouseMiddle) mouse |= 0x4;
            frame.MouseButtons = mouse;

            // 滚轮：记录**本帧新增**的格数（快照给的是累计值）
            int wheel = current.Wheel;
            if (previous != null)
                wheel = current.Wheel - previous.Wheel;
            frame.Wheel = wheel;

            return frame;
        }

    }
}

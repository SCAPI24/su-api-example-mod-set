using Engine;
using Game;
using System;

namespace ScMultiplayer
{
    public class SuComponentInput : Game.ComponentInput
    {
        private void SuUpdateInputFromMouseAndKeyboard(WidgetInput input)
        {
            ScMultiplayer.ModManager.ModParentMethod.InvokeParentMethod(this, "UpdateInputFromMouseAndKeyboard", input);
        }
        private void SuUpdateInputFromGamepad(WidgetInput input)
        {
            ScMultiplayer.ModManager.ModParentMethod.InvokeParentMethod(this, "UpdateInputFromGamepad", input);
        }
        private void SuUpdateInputFromVrControllers(WidgetInput input)
        {
            ScMultiplayer.ModManager.ModParentMethod.InvokeParentMethod(this, "UpdateInputFromVrControllers", input);
        }
        private void SuUpdateInputFromWidgets(WidgetInput input)
        {
            ScMultiplayer.ModManager.ModParentMethod.InvokeParentMethod(this, "UpdateInputFromWidgets", input);
        }
        private PlayerInput Sum_playerInput;
        private ComponentPlayer Sum_componentPlayer;
        private double Sum_lastJumpTime;
        public override void Update(float dt)
        {
            // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.Update
            UpdateNow(dt);
        }
        public void UpdateNow(float dt)
        {
            // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.Update
            var fields = ScMultiplayer.ModManager.ModParentField;
            Type parentType = typeof(ComponentInput);
            Sum_componentPlayer = fields.GetParentField<ComponentPlayer>(this, "m_componentPlayer", parentType);
            Sum_lastJumpTime = fields.GetParentField<double>(this, "m_lastJumpTime", parentType);
            fields.ModifyParentField(this, "m_playerInput", default(PlayerInput), parentType);
            SuUpdateInputFromMouseAndKeyboard(Sum_componentPlayer.GameWidget.Input);
            SuUpdateInputFromGamepad(Sum_componentPlayer.GameWidget.Input);
            SuUpdateInputFromVrControllers(Sum_componentPlayer.GameWidget.Input);
            SuUpdateInputFromWidgets(Sum_componentPlayer.GameWidget.Input);
            Sum_playerInput = fields.GetParentField<PlayerInput>(this, "m_playerInput", parentType);
            if (Sum_playerInput.Jump)
            {
                if (Time.RealTime - Sum_lastJumpTime < 0.3)
                {
                    Sum_playerInput.ToggleCreativeFly = true;
                    Sum_lastJumpTime = 0.0;
                }
                else
                {
                    Sum_lastJumpTime = Time.RealTime;
                }
            }
            Sum_playerInput.CameraMove = Sum_playerInput.Move;
            Sum_playerInput.CameraCrouchMove = Sum_playerInput.CrouchMove;
            Sum_playerInput.CameraLook = Sum_playerInput.Look;
            if (!Window.IsActive || !Sum_componentPlayer.PlayerData.IsReadyForPlaying)
            {
                Sum_playerInput = default(PlayerInput);
            }
            else if (Sum_componentPlayer.ComponentHealth.Health <= 0f || Sum_componentPlayer.ComponentSleep.SleepFactor > 0f || !Sum_componentPlayer.GameWidget.ActiveCamera.IsEntityControlEnabled)
            {
                Sum_playerInput = new PlayerInput
                {
                    CameraMove = Sum_playerInput.CameraMove,
                    CameraCrouchMove = Sum_playerInput.CameraCrouchMove,
                    CameraLook = Sum_playerInput.CameraLook,
                    TimeOfDay = Sum_playerInput.TimeOfDay,
                    Precipitation = Sum_playerInput.Precipitation,
                    Fog = Sum_playerInput.Fog,
                    TakeScreenshot = Sum_playerInput.TakeScreenshot,
                    KeyboardHelp = Sum_playerInput.KeyboardHelp
                };
            }
            else if (Sum_componentPlayer.GameWidget.ActiveCamera.UsesMovementControls)
            {
                Sum_playerInput.Move = Vector3.Zero;
                Sum_playerInput.CrouchMove = Vector3.Zero;
                Sum_playerInput.Look = Vector2.Zero;
                Sum_playerInput.Jump = false;
                Sum_playerInput.ToggleCrouch = false;
                Sum_playerInput.ToggleCreativeFly = false;
            }
            if (Sum_playerInput.Move.LengthSquared() > 1f)
            {
                Sum_playerInput.Move = Vector3.Normalize(Sum_playerInput.Move);
            }
            if (Sum_playerInput.CrouchMove.LengthSquared() > 1f)
            {
                Sum_playerInput.CrouchMove = Vector3.Normalize(Sum_playerInput.CrouchMove);
            }
            if (SplitSourceInventory != null && SplitSourceInventory.GetSlotCount(SplitSourceSlotIndex) == 0)
            {
                SetSplitSourceInventoryAndSlot(null, -1);
            }
            fields.ModifyParentField(this, "m_lastJumpTime", Sum_lastJumpTime, parentType);
            fields.ModifyParentField(this, "m_playerInput", Sum_playerInput, parentType);
        }
    }
}

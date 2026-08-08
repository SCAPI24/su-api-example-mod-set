using Engine;
using Game;

namespace ScMultiplayer
{
    internal static class PlayerInputStatePolicy
    {
        // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.Update
        // Network input may carry only continuous movement/look state here. One-shot UI,
        // inventory and world-control actions are handled by their explicit request messages.
        public static PlayerInput Sanitize(PlayerInput input)
        {
            input.ToggleCreativeFly = false;
            input.ToggleCrouch = false;
            input.ToggleMount = false;
            input.EditItem = false;
            input.ScrollInventory = 0;
            input.SelectInventorySlot = null;
            input.ToggleInventory = false;
            input.ToggleClothing = false;
            input.TakeScreenshot = false;
            input.SwitchCameraMode = false;
            input.TimeOfDay = false;
            input.Lighting = false;
            input.Precipitation = false;
            input.Fog = false;
            input.KeyboardHelp = false;
            input.GamepadHelp = false;
            input.Dig = null;
            input.Aim = null;
            return input;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        // A held snapshot retains only continuous state after a one-shot action is consumed.
        public static PlayerInput CreateHeld(PlayerInput input)
        {
            input.Look = Vector2.Zero;
            input.CameraLook = Vector2.Zero;
            input.VrLook = null;
            input.ToggleCreativeFly = false;
            input.ToggleCrouch = false;
            input.ToggleMount = false;
            input.EditItem = false;
            input.Jump = false;
            input.ScrollInventory = 0;
            input.ToggleInventory = false;
            input.ToggleClothing = false;
            input.TakeScreenshot = false;
            input.SwitchCameraMode = false;
            input.TimeOfDay = false;
            input.Lighting = false;
            input.Precipitation = false;
            input.Fog = false;
            input.KeyboardHelp = false;
            input.GamepadHelp = false;
            input.Dig = null;
            input.Interact = null;
            input.Hit = null;
            input.PickBlockType = null;
            input.Drop = false;
            input.SelectInventorySlot = null;
            return input;
        }
    }
}

using Engine;
using Game;
using System;
using Comms;
namespace ScMultiplayer{
[Serializable]
public class GamePlayerInputMessage : Message
{
    public int PlayerIndex;
    public int Sequence;
    public int ClientTick;
    public Vector3 BodyPosition;
    public Vector3 BodyVelocity;
    public Quaternion BodyRotation;
    public Vector2 LookAngles;
    public PlayerInput PlayerInput;

    public GamePlayerInputMessage() { }

    public GamePlayerInputMessage(int playerIndex, int sequence, int clientTick,
        Vector3 bodyPosition, Vector3 bodyVelocity, Quaternion bodyRotation,
        Vector2 lookAngles, PlayerInput playerInput)
    {
        PlayerIndex = playerIndex;
        Sequence = sequence;
        ClientTick = clientTick;
        BodyPosition = bodyPosition;
        BodyVelocity = bodyVelocity;
        BodyRotation = bodyRotation;
        LookAngles = lookAngles;
        PlayerInput = playerInput;
    }

    protected override void Read(SuReader reader)
    {
        PlayerIndex = reader.ReadInt32();
        Sequence = reader.ReadInt32();
        ClientTick = reader.ReadInt32();
        BodyPosition = reader.ReadVector3(reader);
        BodyVelocity = reader.ReadVector3(reader);
        BodyRotation = reader.ReadQuaternion(reader);
        LookAngles = reader.ReadVector2(reader);

        // 读取基本向量
        PlayerInput.Look = reader.ReadVector2(reader);
        PlayerInput.Move = reader.ReadVector3(reader);
        PlayerInput.CrouchMove = reader.ReadVector3(reader);
        PlayerInput.CameraLook = reader.ReadVector2(reader);
        PlayerInput.CameraMove = reader.ReadVector3(reader);
        PlayerInput.CameraCrouchMove = reader.ReadVector3(reader);

        // 读取可空向量
        PlayerInput.VrMove = reader.ReadBoolean() ? reader.ReadVector3(reader) : (Vector3?)null;
        PlayerInput.VrLook = reader.ReadBoolean() ? reader.ReadVector2(reader) : (Vector2?)null;

        // 读取布尔值
        PlayerInput.ToggleCreativeFly = reader.ReadBoolean();
        PlayerInput.ToggleCrouch = reader.ReadBoolean();
        PlayerInput.ToggleMount = reader.ReadBoolean();
        PlayerInput.EditItem = reader.ReadBoolean();
        PlayerInput.Jump = reader.ReadBoolean();
        PlayerInput.ToggleInventory = reader.ReadBoolean();
        PlayerInput.ToggleClothing = reader.ReadBoolean();
        PlayerInput.TakeScreenshot = reader.ReadBoolean();
        PlayerInput.SwitchCameraMode = reader.ReadBoolean();
        PlayerInput.TimeOfDay = reader.ReadBoolean();
        PlayerInput.Lighting = reader.ReadBoolean();
        PlayerInput.Precipitation = reader.ReadBoolean();
        PlayerInput.Fog = reader.ReadBoolean();
        PlayerInput.KeyboardHelp = reader.ReadBoolean();
        PlayerInput.GamepadHelp = reader.ReadBoolean();
        PlayerInput.Drop = reader.ReadBoolean();

        // 读取整数值
        PlayerInput.ScrollInventory = reader.ReadInt32();
        PlayerInput.SelectInventorySlot = reader.ReadBoolean() ? reader.ReadInt32() : (int?)null;

        // 读取可空射线
        PlayerInput.Dig = reader.ReadBoolean() ? reader.ReadRay3(reader) : (Ray3?)null;
        PlayerInput.Hit = reader.ReadBoolean() ? reader.ReadRay3(reader) : (Ray3?)null;
        PlayerInput.Aim = reader.ReadBoolean() ? reader.ReadRay3(reader) : (Ray3?)null;
        PlayerInput.Interact = reader.ReadBoolean() ? reader.ReadRay3(reader) : (Ray3?)null;
        PlayerInput.PickBlockType = reader.ReadBoolean() ? reader.ReadRay3(reader) : (Ray3?)null;
    }

    protected override void Write(SuWriter writer)
    {
        writer.WriteInt32(PlayerIndex);
        writer.WriteInt32(Sequence);
        writer.WriteInt32(ClientTick);
        writer.WriteVector3(writer, BodyPosition);
        writer.WriteVector3(writer, BodyVelocity);
        writer.WriteQuaternion(writer, BodyRotation);
        writer.WriteVector2(writer, LookAngles);

            // 写入基本向量
            writer.WriteVector2(writer, PlayerInput.Look);
            writer.WriteVector3(writer, PlayerInput.Move);
            writer.WriteVector3(writer, PlayerInput.CrouchMove);
            writer.WriteVector2(writer, PlayerInput.CameraLook);
            writer.WriteVector3(writer, PlayerInput.CameraMove);
            writer.WriteVector3(writer, PlayerInput.CameraCrouchMove);

        // 写入可空向量
        writer.WriteBoolean(PlayerInput.VrMove.HasValue);
        if (PlayerInput.VrMove.HasValue) writer.WriteVector3(writer, PlayerInput.VrMove.Value);

        writer.WriteBoolean(PlayerInput.VrLook.HasValue);
        if (PlayerInput.VrLook.HasValue) writer.WriteVector2(writer, PlayerInput.VrLook.Value);

        // 写入布尔值
        writer.WriteBoolean(PlayerInput.ToggleCreativeFly);
        writer.WriteBoolean(PlayerInput.ToggleCrouch);
        writer.WriteBoolean(PlayerInput.ToggleMount);
        writer.WriteBoolean(PlayerInput.EditItem);
        writer.WriteBoolean(PlayerInput.Jump);
        writer.WriteBoolean(PlayerInput.ToggleInventory);
        writer.WriteBoolean(PlayerInput.ToggleClothing);
        writer.WriteBoolean(PlayerInput.TakeScreenshot);
        writer.WriteBoolean(PlayerInput.SwitchCameraMode);
        writer.WriteBoolean(PlayerInput.TimeOfDay);
        writer.WriteBoolean(PlayerInput.Lighting);
        writer.WriteBoolean(PlayerInput.Precipitation);
        writer.WriteBoolean(PlayerInput.Fog);
        writer.WriteBoolean(PlayerInput.KeyboardHelp);
        writer.WriteBoolean(PlayerInput.GamepadHelp);
        writer.WriteBoolean(PlayerInput.Drop);

        // 写入整数值
        writer.WriteInt32(PlayerInput.ScrollInventory);

        writer.WriteBoolean(PlayerInput.SelectInventorySlot.HasValue);
        if (PlayerInput.SelectInventorySlot.HasValue) writer.WriteInt32(PlayerInput.SelectInventorySlot.Value);

        // 写入可空射线
        writer.WriteBoolean(PlayerInput.Dig.HasValue);
        if (PlayerInput.Dig.HasValue) writer.WriteRay3(writer, PlayerInput.Dig.Value);

        writer.WriteBoolean(PlayerInput.Hit.HasValue);
        if (PlayerInput.Hit.HasValue) writer.WriteRay3(writer, PlayerInput.Hit.Value);

        writer.WriteBoolean(PlayerInput.Aim.HasValue);
        if (PlayerInput.Aim.HasValue) writer.WriteRay3(writer, PlayerInput.Aim.Value);

        writer.WriteBoolean(PlayerInput.Interact.HasValue);
        if (PlayerInput.Interact.HasValue) writer.WriteRay3(writer, PlayerInput.Interact.Value);

        writer.WriteBoolean(PlayerInput.PickBlockType.HasValue);
        if (PlayerInput.PickBlockType.HasValue) writer.WriteRay3(writer, PlayerInput.PickBlockType.Value);
    }

}
}

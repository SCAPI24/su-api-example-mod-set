namespace ScMultiplayer.Ports
{
    // Source: Mod/ScMultiplayer/Func/Component/MultiplayerUiComponent.cs:Update
    // UI adapters depend on commands, not on the concrete multiplayer runtime or transport.
    internal interface IMultiplayerUiCommandPort
    {
        bool IsInRoom { get; }

        void ShowCreateRoomDialog();

        void ShowTalkDialog();

        void ShowMultiplayerManagementDialog();

        void ShowJoinedPlayerInformation();
    }
}

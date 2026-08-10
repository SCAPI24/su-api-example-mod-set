namespace ScMultiplayer.Core
{
    // Source: Comms/Drt/Client.cs:Client.GameCreated and Client.GameJoined
    // A pending create/join is not a multiplayer session until the transport confirms it.
    internal enum MultiplayerSessionMode
    {
        Offline,
        Host,
        Client
    }
}

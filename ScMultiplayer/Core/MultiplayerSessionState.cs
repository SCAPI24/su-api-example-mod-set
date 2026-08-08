using System;

namespace ScMultiplayer.Core
{
    internal enum MultiplayerSessionRole
    {
        Detached,
        Host,
        Client
    }

    internal enum MultiplayerSessionPhase
    {
        Detached,
        Connecting,
        Joining,
        Ready,
        Leaving
    }

    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.OnLoad
    // This state is deliberately limited to session metadata. Game objects remain owned by
    // the existing game adapters and business modules.
    internal sealed class MultiplayerSessionState
    {
        public MultiplayerSessionRole Role { get; private set; }

        public MultiplayerSessionPhase Phase { get; private set; }

        public bool IsConnected { get; private set; }

        public int ClientId { get; private set; } = -1;

        public int GameId { get; private set; } = -1;

        public string ServerEndpoint { get; private set; }

        public string WorldName { get; private set; }

        public bool IsWorldReady { get; private set; }

        public long Generation { get; private set; }

        public void Update(bool isHost, bool isConnected)
        {
            if (!isConnected)
            {
                if (IsConnected || Role != MultiplayerSessionRole.Detached ||
                    ClientId >= 0 || GameId >= 0)
                    Reset();
                return;
            }

            MultiplayerSessionRole role = isHost
                ? MultiplayerSessionRole.Host
                : isConnected ? MultiplayerSessionRole.Client : MultiplayerSessionRole.Detached;
            MultiplayerSessionPhase phase = isConnected
                ? (isHost || IsWorldReady
                    ? MultiplayerSessionPhase.Ready
                    : MultiplayerSessionPhase.Joining)
                : MultiplayerSessionPhase.Detached;

            if (Role == role && Phase == phase && IsConnected == isConnected)
                return;

            Role = role;
            Phase = phase;
            IsConnected = isConnected;
            Generation++;
        }

        public void EnterRoom(int clientId, int gameId, bool isHost,
            string serverEndpoint, string worldName)
        {
            MultiplayerSessionRole role = isHost
                ? MultiplayerSessionRole.Host
                : MultiplayerSessionRole.Client;
            MultiplayerSessionPhase phase = isHost
                ? MultiplayerSessionPhase.Ready
                : MultiplayerSessionPhase.Joining;

            if (Role == role && Phase == phase && IsConnected &&
                ClientId == clientId && GameId == gameId &&
                string.Equals(ServerEndpoint, serverEndpoint, StringComparison.Ordinal) &&
                string.Equals(WorldName, worldName, StringComparison.Ordinal))
                return;

            Role = role;
            Phase = phase;
            IsConnected = true;
            ClientId = clientId;
            GameId = gameId;
            ServerEndpoint = serverEndpoint;
            WorldName = worldName;
            IsWorldReady = isHost;
            Generation++;
        }

        public void MarkWorldReady()
        {
            if (!IsConnected || IsWorldReady)
                return;
            Phase = MultiplayerSessionPhase.Ready;
            IsWorldReady = true;
            Generation++;
        }

        public void Reset()
        {
            Role = MultiplayerSessionRole.Detached;
            Phase = MultiplayerSessionPhase.Detached;
            IsConnected = false;
            ClientId = -1;
            GameId = -1;
            ServerEndpoint = null;
            WorldName = null;
            IsWorldReady = false;
            Generation++;
        }
    }
}

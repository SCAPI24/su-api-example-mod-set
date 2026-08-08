using System;
using System.Collections.Generic;

namespace ScMultiplayer.Core
{
    internal enum PlayerConnectionPhase
    {
        Reserved,
        Joining,
        Ready,
        Disconnected
    }

    internal sealed class PlayerConnectionInfo
    {
        public int ClientId { get; internal set; }
        public int PlayerIndex { get; internal set; }
        public string Endpoint { get; internal set; }
        public bool IsHost { get; internal set; }
        public PlayerConnectionPhase Phase { get; internal set; }
        public double LastTransitionTime { get; internal set; }
    }

    // Source: Mod/ScMultiplayer/doc/MODULAR-REFACTOR-PLAN.md:6.2 状态所有权
    // This registry owns connection lifecycle metadata only. Player index assignment remains in
    // the legacy mapping adapter until its callers have been migrated and behavior is covered.
    internal sealed class PlayerConnectionRegistry
    {
        private readonly Dictionary<int, PlayerConnectionInfo> m_connections =
            new Dictionary<int, PlayerConnectionInfo>();

        public int ActiveCount
        {
            get
            {
                int count = 0;
                foreach (PlayerConnectionInfo connection in m_connections.Values)
                {
                    if (connection.Phase != PlayerConnectionPhase.Disconnected)
                        count++;
                }
                return count;
            }
        }

        public void Register(int clientId, int playerIndex, string endpoint, bool isHost,
            PlayerConnectionPhase phase, double now)
        {
            if (clientId < 0)
                return;

            if (!m_connections.TryGetValue(clientId, out PlayerConnectionInfo connection))
            {
                connection = new PlayerConnectionInfo();
                m_connections.Add(clientId, connection);
            }

            connection.ClientId = clientId;
            connection.PlayerIndex = playerIndex;
            connection.Endpoint = endpoint;
            connection.IsHost = isHost;
            connection.Phase = phase;
            connection.LastTransitionTime = now;
        }

        public bool TryTransition(int clientId, PlayerConnectionPhase phase, double now)
        {
            if (!m_connections.TryGetValue(clientId, out PlayerConnectionInfo connection))
                return false;
            connection.Phase = phase;
            connection.LastTransitionTime = now;
            return true;
        }

        public bool MarkDisconnected(int clientId, double now) =>
            TryTransition(clientId, PlayerConnectionPhase.Disconnected, now);

        public bool TryGet(int clientId, out PlayerConnectionInfo connection) =>
            m_connections.TryGetValue(clientId, out connection);

        public PlayerConnectionInfo[] Snapshot()
        {
            var snapshot = new PlayerConnectionInfo[m_connections.Count];
            int index = 0;
            foreach (PlayerConnectionInfo source in m_connections.Values)
            {
                snapshot[index++] = new PlayerConnectionInfo
                {
                    ClientId = source.ClientId,
                    PlayerIndex = source.PlayerIndex,
                    Endpoint = source.Endpoint,
                    IsHost = source.IsHost,
                    Phase = source.Phase,
                    LastTransitionTime = source.LastTransitionTime
                };
            }
            return snapshot;
        }

        public void Reset() => m_connections.Clear();
    }
}

using System;
using ScMultiplayer.Ports;

namespace ScMultiplayer.Transport
{
    // Source: Mod/ScMultiplayer/Networking/NetworkMessageSender.cs:SendScheduledMessage
    // Policy is isolated from Comms. The current sender remains authoritative; this coordinator
    // is the single future port for circuit/action/checkpoint reliability and adds no queue.
    internal sealed class ReliableChannelCoordinator : IReliableChannel
    {
        private readonly INetworkTransport m_transport;
        private readonly Func<int> m_pendingCount;

        public ReliableChannelCoordinator(INetworkTransport transport,
            Func<int> pendingCount = null)
        {
            m_transport = transport ?? throw new ArgumentNullException(nameof(transport));
            m_pendingCount = pendingCount;
        }

        public int PendingCount => Math.Max(0, m_pendingCount?.Invoke() ?? 0);

        public void Send(int targetClientId, byte[] payload, bool sequenced, bool latest)
        {
            if (payload == null || payload.Length == 0)
                return;
            m_transport.SendDirectInput(targetClientId, payload, sequenced, latest);
        }

        public void Reset(int clientId)
        {
            // Comms owns ACK/retry state. Reset is intentionally a policy boundary and cannot
            // clear another client's transport window.
        }
    }
}

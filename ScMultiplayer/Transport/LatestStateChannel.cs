using System.Collections.Generic;

namespace ScMultiplayer.Transport
{
    // Source: Mod/ScMultiplayer/Networking/NetworkMessageSender.cs:SendPlayerPositionBatch
    // Replaceable state is coalesced by key and never enters the reliable queue.
    internal sealed class LatestStateChannel<TKey>
    {
        private readonly Dictionary<TKey, byte[]> m_latest =
            new Dictionary<TKey, byte[]>();

        public int Count => m_latest.Count;

        public void Publish(TKey key, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return;
            m_latest[key] = payload;
        }

        public bool TryTake(TKey key, out byte[] payload)
        {
            if (!m_latest.TryGetValue(key, out payload))
                return false;
            m_latest.Remove(key);
            return true;
        }

        public void Clear() => m_latest.Clear();

        public void Reset(TKey key) => m_latest.Remove(key);
    }
}

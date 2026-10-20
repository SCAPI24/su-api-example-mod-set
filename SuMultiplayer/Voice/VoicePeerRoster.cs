using System;
using System.Collections.Generic;

namespace SuMultiplayer.Voice
{
    /// <summary>一端的语音状态（主机在 client_id = 0）。</summary>
    public struct VoicePeerState
    {
        public int ClientId;
        public string Name;
        public bool Supported;
        public bool MicEnabled;
        /// <summary>
        /// VoiceState 声明的编码档位（线上 id：1=high 2=standard 3=low 4=ultra；
        /// 0 = 还没声明 / 旧端）。主机据此拒绝与该说话者声明不一致的包。
        /// </summary>
        public int Format;

        public bool IsHost => ClientId == 0;
    }

    /// <summary>
    /// 语音名册（纯逻辑）：主机端是权威（ClientJoined / VoiceState 驱动），
    /// 客户端用主机下发的 VoiceRoster 覆盖。自检直接编译这一份源码。
    /// 无静态状态。
    /// </summary>
    public sealed class VoicePeerRoster
    {
        private readonly Dictionary<int, VoicePeerState> m_byId =
            new Dictionary<int, VoicePeerState>();

        public int Count => m_byId.Count;

        public bool Contains(int clientId) => m_byId.ContainsKey(clientId);

        public bool TryGet(int clientId, out VoicePeerState peer) =>
            m_byId.TryGetValue(clientId, out peer);

        public void Clear() => m_byId.Clear();

        public void SetHost(string name)
        {
            m_byId[0] = new VoicePeerState
            {
                ClientId = 0,
                Name = string.IsNullOrWhiteSpace(name) ? "Host" : name,
                Supported = true,
                MicEnabled = m_byId.TryGetValue(0, out VoicePeerState previous) && previous.MicEnabled,
                Format = m_byId.TryGetValue(0, out VoicePeerState formatPeer) ? formatPeer.Format : 0
            };
        }

        public void Upsert(int clientId, string name, bool supported, bool micEnabled)
        {
            int format = m_byId.TryGetValue(clientId, out VoicePeerState previous)
                ? previous.Format
                : 0;
            Upsert(clientId, name, supported, micEnabled, format);
        }

        public void Upsert(int clientId, string name, bool supported, bool micEnabled, int format)
        {
            if (clientId < 0) throw new ArgumentOutOfRangeException(nameof(clientId));
            m_byId[clientId] = new VoicePeerState
            {
                ClientId = clientId,
                Name = string.IsNullOrWhiteSpace(name) ? "Player " + clientId : name,
                Supported = supported,
                MicEnabled = micEnabled,
                Format = format
            };
        }

        public bool SetState(int clientId, bool supported, bool micEnabled)
        {
            if (!m_byId.TryGetValue(clientId, out VoicePeerState peer)) return false;
            peer.Supported = supported;
            peer.MicEnabled = micEnabled;
            m_byId[clientId] = peer;
            return true;
        }

        /// <summary>记录该端声明的档位；-1 = 端不存在。</summary>
        public bool SetFormat(int clientId, int format)
        {
            if (!m_byId.TryGetValue(clientId, out VoicePeerState peer)) return false;
            peer.Format = format;
            m_byId[clientId] = peer;
            return true;
        }

        public bool SetHostFormat(int format)
        {
            if (!m_byId.TryGetValue(0, out VoicePeerState peer)) return false;
            peer.Format = format;
            m_byId[0] = peer;
            return true;
        }

        public bool SetHostMic(bool micEnabled)
        {
            if (!m_byId.TryGetValue(0, out VoicePeerState peer)) return false;
            peer.MicEnabled = micEnabled;
            peer.Supported = true;
            m_byId[0] = peer;
            return true;
        }

        public bool Remove(int clientId) => m_byId.Remove(clientId);

        /// <summary>按 client_id 排序（主机 0 在最前），给 HUD / 测试稳定顺序。</summary>
        public VoicePeerState[] Snapshot()
        {
            var list = new List<VoicePeerState>(m_byId.Values);
            list.Sort((left, right) => left.ClientId.CompareTo(right.ClientId));
            return list.ToArray();
        }

        public int[] ClientIds()
        {
            var ids = new int[m_byId.Count];
            m_byId.Keys.CopyTo(ids, 0);
            Array.Sort(ids);
            return ids;
        }

        public string Describe()
        {
            VoicePeerState[] peers = Snapshot();
            var parts = new string[peers.Length];
            for (int i = 0; i < peers.Length; i++)
            {
                VoicePeerState peer = peers[i];
                parts[i] = peer.ClientId + ":" + peer.Name + (peer.Supported ? "" : "(off)") +
                    (peer.MicEnabled ? "" : "(mute)");
            }
            return "peers=" + peers.Length + " [" + string.Join(",", parts) + "]";
        }
    }

    /// <summary>
    /// 每监听者 → 被静音说话者 的表（主机权威）。客户端发 VoiceMuteRequest，
    /// 主机校验后**不再把该说话者的包中继给这个监听者**（省带宽），并回 MuteResult。
    ///
    /// 合法 id：host = 0；客户端 = 传输层 clientId（&gt; 0）。监听者不能静音自己。
    /// 无静态状态。
    /// </summary>
    public sealed class VoiceMuteTable
    {
        private readonly Dictionary<int, HashSet<int>> m_byListener =
            new Dictionary<int, HashSet<int>>();

        public int Count
        {
            get
            {
                int total = 0;
                foreach (HashSet<int> set in m_byListener.Values) total += set.Count;
                return total;
            }
        }

        public int ListenerCount => m_byListener.Count;

        public bool IsMuted(int listenerClientId, int speakerClientId)
        {
            if (!m_byListener.TryGetValue(listenerClientId, out HashSet<int> set)) return false;
            return set.Contains(speakerClientId);
        }

        public bool Set(int listenerClientId, int speakerClientId, bool muted, out string reason)
        {
            reason = null;
            if (listenerClientId <= 0)
            {
                reason = "voice-mute-listener-" + listenerClientId;
                return false;
            }
            if (speakerClientId < 0)
            {
                reason = "voice-mute-target-" + speakerClientId;
                return false;
            }
            if (listenerClientId == speakerClientId)
            {
                reason = "voice-mute-self";
                return false;
            }
            if (!muted)
            {
                if (m_byListener.TryGetValue(listenerClientId, out HashSet<int> existing))
                {
                    existing.Remove(speakerClientId);
                    if (existing.Count == 0) m_byListener.Remove(listenerClientId);
                }
                return true;
            }
            if (!m_byListener.TryGetValue(listenerClientId, out HashSet<int> set))
            {
                set = new HashSet<int>();
                m_byListener[listenerClientId] = set;
            }
            set.Add(speakerClientId);
            return true;
        }

        public void RemoveListener(int clientId) => m_byListener.Remove(clientId);

        public void RemoveSpeaker(int clientId)
        {
            if (clientId < 0) return;
            foreach (HashSet<int> set in m_byListener.Values) set.Remove(clientId);
            var empty = new List<int>();
            foreach (KeyValuePair<int, HashSet<int>> pair in m_byListener)
                if (pair.Value.Count == 0) empty.Add(pair.Key);
            for (int i = 0; i < empty.Count; i++) m_byListener.Remove(empty[i]);
        }

        public void Clear() => m_byListener.Clear();
    }
}

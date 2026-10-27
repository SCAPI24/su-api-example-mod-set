using System;
using System.Collections.Generic;

namespace SuMultiplayer.Session
{
    /// <summary>主机在房间发现应答里附带的旧列表行字段（大小/保存时间/环境/版本）。</summary>
    public readonly struct RoomWorldDetails
    {
        public readonly long SizeBytes;
        public readonly DateTime LastSaveTimeUtc;
        public readonly string EnvironmentMode;
        public readonly string SerializationVersion;

        public RoomWorldDetails(long sizeBytes, DateTime lastSaveTimeUtc, string environmentMode,
            string serializationVersion)
        {
            SizeBytes = sizeBytes;
            LastSaveTimeUtc = lastSaveTimeUtc;
            EnvironmentMode = environmentMode ?? string.Empty;
            SerializationVersion = serializationVersion ?? string.Empty;
        }
    }

    /// <summary>房间来源：局域网广播 / 服务器目录 / 显式端点 / 个人网络世界。</summary>
    public enum RoomSource : byte
    {
        Lan = 0,
        Directory = 1,
        Explicit = 2,
        Personal = 3
    }

    /// <summary>房间列表条目（跨来源统一模型）。</summary>
    public sealed class RoomEntry
    {
        public string Key;              // 稳定键：来源 + 地址 + 端口
        public string Name;
        public string HostAddress;
        public int Port;
        public RoomSource Source;
        /// <summary>列表行名称后面括号里的来源地址：ServerDns 显示解析的 DNS 名，
        /// 个人服务器显示用户填写的地址（含端口）。LAN 行留空。</summary>
        public string DisplayAddress;
        /// <summary>个人服务器/目录服务是否在线。离线个人服务器行保留在列表里（便于删除，
        /// 旧实现显示 "Net World | Offline (Ping -)"）。</summary>
        public bool Online = true;
        /// <summary>离线也保留在列表里（个人服务器用；RoomDirectory 的 stale 清理会跳过它）。</summary>
        public bool Persistent;
        public int PingMs = -1;
        public int PlayerCount;
        public int MaxPlayers;
        public string WorldName;
        public long WorldSizeBytes;
        public string GameMode;
        public string EnvironmentMode;
        public DateTime LastSaveTimeUtc = DateTime.MinValue;
        public string SerializationVersion = string.Empty;
        public long LastSeenMs;
        public bool ProtocolCompatible = true;

        /// <summary>
        /// 列表里的来源标注：局域网 = 在线，目录/显式 = 服务。
        /// **必须用英文**：Play 屏是英文界面（Survivalcraft 的界面字体没有中文字形，
        /// 中文会渲染成下划线）。旧实现 SuPlayScreen 用的就是 "Online" / "Service"。
        /// </summary>
        public string SourceLabel => Source == RoomSource.Lan ? "LAN" :
            Source == RoomSource.Personal ? (Online ? "Online" : "Offline") : "Service";

        public string Describe()
        {
            string ping = PingMs >= 0 ? "延迟 " + PingMs + " ms" : "延迟 ?";
            string players = PlayerCount + "/" + (MaxPlayers > 0 ? MaxPlayers.ToString() : "?");
            string size = WorldSizeBytes > 0 ? (WorldSizeBytes / 1024) + "KB" : "-";
            return Name + " | " + size + " | " + players + " 名玩家 | " + GameMode + " | " +
                EnvironmentMode + " | " + SourceLabel + "（" + ping + "）";
        }
    }

    /// <summary>
    /// 房间目录：多来源去重、心跳过期、排序，以及"刷新时保住选择索引"。
    /// 迁移自旧实现 SuPlayScreen 的行为：发现包临时丢失时不清空列表、不跳选中项。
    /// 演示页标记：DSC01。
    /// </summary>
    public sealed class RoomDirectory
    {
        private readonly Dictionary<string, RoomEntry> m_entries = new Dictionary<string, RoomEntry>(StringComparer.Ordinal);
        private readonly List<string> m_order = new List<string>();
        private string m_selectedKey;

        /// <summary>超过这个时间没再被发现的房间视为离线（不立刻删除，便于"选择不跳"。</summary>
        public int StaleMs { get; set; } = 15000;

        public int Count => m_entries.Count;

        public string SelectedKey => m_selectedKey;

        public int SelectedIndex
        {
            get
            {
                IReadOnlyList<RoomEntry> view = View;
                for (int i = 0; i < view.Count; i++)
                {
                    if (string.Equals(view[i].Key, m_selectedKey, StringComparison.Ordinal)) return i;
                }
                return view.Count == 0 ? -1 : 0;
            }
        }

        /// <summary>发现/更新一个房间。</summary>
        public void Upsert(RoomEntry entry, long nowMs)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key)) return;
            entry.LastSeenMs = nowMs;
            if (!m_entries.ContainsKey(entry.Key)) m_order.Add(entry.Key);
            m_entries[entry.Key] = entry;
            if (m_selectedKey == null) m_selectedKey = entry.Key;
        }

        /// <summary>清掉长时间未再出现的房间；若被清掉的正是当前选中项，按原位置就近重选。</summary>
        public int RemoveStale(long nowMs)
        {
            var stale = new List<string>();
            foreach (KeyValuePair<string, RoomEntry> pair in m_entries)
            {
                if (pair.Value.Persistent) continue;
                if (nowMs - pair.Value.LastSeenMs > StaleMs) stale.Add(pair.Key);
            }
            if (stale.Count == 0) return 0;

            int selectedPosition = SelectedIndex;
            foreach (string key in stale)
            {
                m_entries.Remove(key);
                m_order.Remove(key);
            }
            if (m_selectedKey != null && !m_entries.ContainsKey(m_selectedKey))
            {
                IReadOnlyList<RoomEntry> view = View;
                if (view.Count == 0) m_selectedKey = null;
                else m_selectedKey = view[Math.Min(Math.Max(selectedPosition, 0), view.Count - 1)].Key;
            }
            return stale.Count;
        }

        public void Select(int index)
        {
            IReadOnlyList<RoomEntry> view = View;
            if (index < 0 || index >= view.Count) return;
            m_selectedKey = view[index].Key;
        }

        public void SelectByKey(string key)
        {
            if (key != null && m_entries.ContainsKey(key)) m_selectedKey = key;
        }

        /// <summary>删除一个房间条目（个人服务器删除用）。</summary>
        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key) || !m_entries.Remove(key)) return false;
            m_order.Remove(key);
            if (string.Equals(m_selectedKey, key, StringComparison.Ordinal))
                m_selectedKey = m_order.Count > 0 ? m_order[0] : null;
            return true;
        }

        public void Clear()
        {
            m_entries.Clear();
            m_order.Clear();
            m_selectedKey = null;
        }

        /// <summary>展示顺序：协议兼容优先 → 在线优先 → 延迟低优先 → 名称。</summary>
        public IReadOnlyList<RoomEntry> View
        {
            get
            {
                var list = new List<RoomEntry>(m_entries.Count);
                foreach (string key in m_order)
                {
                    if (m_entries.TryGetValue(key, out RoomEntry entry)) list.Add(entry);
                }
                list.Sort((a, b) =>
                {
                    if (a.ProtocolCompatible != b.ProtocolCompatible) return a.ProtocolCompatible ? -1 : 1;
                    bool aLan = a.Source == RoomSource.Lan, bLan = b.Source == RoomSource.Lan;
                    if (aLan != bLan) return aLan ? -1 : 1;
                    int pingA = a.PingMs < 0 ? int.MaxValue : a.PingMs;
                    int pingB = b.PingMs < 0 ? int.MaxValue : b.PingMs;
                    if (pingA != pingB) return pingA.CompareTo(pingB);
                    return string.CompareOrdinal(a.Name, b.Name);
                });
                return list;
            }
        }

        public bool TryGetSelected(out RoomEntry entry)
        {
            entry = null;
            return m_selectedKey != null && m_entries.TryGetValue(m_selectedKey, out entry);
        }
    }
}

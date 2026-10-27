using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using Proto = SuMultiplayer.Protocol;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 主机权威复活点表同步（PLAYER_SPAWN=116，2026-10-27，方案 B）。
    ///
    /// 每个角色保留自己的复活点，主机是唯一权威来源：
    ///   · 主机本地角色继续以 Project.xml 为权威（不搬进 SuMultiplayerPlayers.xml，
    ///     保证没有联机 Mod 时原版仍可正常进入世界）；
    ///   · 客户端角色以主机 SuMultiplayerPlayers.xml 记录里的 SpawnPosition 为权威；
    ///   · 主机把在线角色复活点表发给所有端，客户端只读镜像；
    ///   · 无磁铁时 SuCompassBlock 用同一张表 + 手部位置选目标，因此同一角色在任何
    ///     一端看到的指南针指向同一点。
    ///
    /// 新客户端加入发 full 快照；之后每 500ms 对比主机表，变化/新角色/离场发 delta。
    /// </summary>
    public sealed class SpawnPointSyncModule : IMultiplayerModule, ISuMPMessageModule
    {
        public const string ModuleIdText = "player-spawn";
        private const long HostCaptureIntervalMs = 500;
        private const long ProblemLogIntervalMs = 10000;

        private sealed class HostEntry
        {
            public Vector3 Spawn;
            public string Identity = string.Empty;
        }

        private readonly ISpawnPointProvider m_provider;
        private readonly Func<double> m_clock;
        private IModuleContext m_context;
        private ITransportPort m_transport;
        private IMessageRegistryPort m_messages;
        private IPeerBroadcastPort m_broadcast;
        private IWorldSnapshotInvalidatorPort m_worldInvalidator;
        private bool m_attached;

        private readonly Dictionary<PlayerKey, HostEntry> m_hostEntries =
            new Dictionary<PlayerKey, HostEntry>();
        private readonly Dictionary<PlayerKey, HostEntry> m_currentHostTable =
            new Dictionary<PlayerKey, HostEntry>();
        private readonly Dictionary<PlayerKey, Vector3> m_clientEntries =
            new Dictionary<PlayerKey, Vector3>();
        private readonly HashSet<PlayerKey> m_clientPending = new HashSet<PlayerKey>();
        /// <summary>主机：需要在下一拍重试 full 快照的客户端（首次 TrySendTo 可能背压失败）。</summary>
        private readonly HashSet<int> m_pendingFullSnapshotClients = new HashSet<int>();
        private readonly Dictionary<string, long> m_lastProblemMs = new Dictionary<string, long>();

        private long m_lastHostCaptureMs = long.MinValue / 2;
        private uint m_hostRevision;
        private uint m_clientRevision;
        private int m_lastLocalClientId = int.MinValue;
        private bool m_hostSnapshotBroadcast;

        private long m_hostSnapshotsSent;
        private long m_hostDeltasSent;
        private long m_hostEntriesSent;
        private long m_clientSnapshotsReceived;
        private long m_clientDeltasReceived;
        private long m_clientEntriesApplied;
        private long m_clientStaleDropped;
        private long m_applyFailures;

        public SpawnPointSyncModule(ISpawnPointProvider provider, Func<double> clock)
        {
            m_provider = provider ?? throw new ArgumentNullException(nameof(provider));
            m_clock = clock ?? throw new ArgumentNullException(nameof(clock));
            m_descriptor = new ModuleDescriptor(ModuleIdText, "主机权威复活点表同步")
            {
                RequiresCapabilities = new[] { "transport" },
                ProvidesCapabilities = new[] { "player.spawn" },
                HotPluggable = true
            };
        }

        private readonly ModuleDescriptor m_descriptor;
        public ModuleDescriptor Descriptor => m_descriptor;

        public string ModuleId => m_descriptor.Id;

        public void Register(MessageRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            registry.UnregisterOwner(ModuleId);
            registry.Register((int)Proto.MessageType.PlayerSpawn,
                Proto.PlayerSpawnState.Parser,
                new DelegateMessageHandler<Proto.PlayerSpawnState>(OnPlayerSpawn),
                ModuleId);
        }

        public void Attach(IModuleContext context)
        {
            m_context = context;
            if (!context.TryGetPort(out m_transport))
                Error("没有拿到传输端口（transport）→ 复活点表无法收发");
            if (!context.TryGetPort(out m_messages))
                Error("没有拿到消息注册表（IMessageRegistryPort）→ 无法注册 PLAYER_SPAWN（116）");
            if (!context.TryGetPort(out m_broadcast))
                Error("没有拿到广播端口（IPeerBroadcastPort）→ 主机无法广播复活点表");
            context.TryGetPort(out m_worldInvalidator);
            if (m_transport != null)
            {
                m_transport.ClientJoined += HandleClientJoined;
                m_transport.ClientLeft += HandleClientLeft;
            }
            if (m_messages != null)
            {
                m_messages.Registry.RegisterModule(this);
            }
            else
            {
                Error("没有消息注册表 → PLAYER_SPAWN（116）收不到");
            }
            m_attached = true;
            Info("复活点表同步就绪：主机权威表 → 新客户端 full 快照 + 全端 delta；客户端只镜像。");
        }

        public void Detach(ModuleDetachReason reason)
        {
            if (m_transport != null)
            {
                try { m_transport.ClientJoined -= HandleClientJoined; } catch (Exception) { }
                try { m_transport.ClientLeft -= HandleClientLeft; } catch (Exception) { }
            }
            if (m_messages != null)
            {
                try { m_messages.Registry.UnregisterOwner(ModuleIdText); } catch (Exception) { }
                m_messages = null;
            }
            m_hostEntries.Clear();
            m_currentHostTable.Clear();
            m_pendingFullSnapshotClients.Clear();
            m_clientEntries.Clear();
            m_clientPending.Clear();
            m_lastProblemMs.Clear();
            m_attached = false;
            Info("复活点表同步下线（" + reason + "）：" + StatusLine());
        }

        public void Tick(in ModuleTickContext tick)
        {
            if (!m_attached) return;
            bool hosting = m_broadcast != null && m_broadcast.IsHosting;
            if (hosting)
            {
                long nowMs = (long)(m_clock() * 1000.0);
                if (nowMs - m_lastHostCaptureMs >= HostCaptureIntervalMs)
                {
                    m_lastHostCaptureMs = nowMs;
                    CaptureAndPublishHostTable();
                }
            }
            else if (m_transport != null && m_transport.IsConnected)
            {
                RefreshClientSession();
                ApplyPendingClientEntries();
            }
        }

        public string StatusLine() =>
            "player-spawn: role=" + (m_broadcast?.IsHosting == true ? "host" : "client") +
            " hostSnapshots=" + m_hostSnapshotsSent +
            " hostDeltas=" + m_hostDeltasSent +
            " hostEntries=" + m_hostEntriesSent +
            " clientSnapshots=" + m_clientSnapshotsReceived +
            " clientDeltas=" + m_clientDeltasReceived +
            " clientApplied=" + m_clientEntriesApplied +
            " pending=" + m_clientPending.Count +
            " staleDropped=" + m_clientStaleDropped +
            " applyFailures=" + m_applyFailures +
            " rev=" + (m_broadcast?.IsHosting == true ? m_hostRevision : m_clientRevision);

        // ---------------------------------------------------------------- 主机侧

        private void HandleClientJoined(int clientId, string name, string identity, bool rejoin)
        {
            if (!m_attached || m_broadcast == null || !m_broadcast.IsHosting) return;
            // 新连接要下载最新世界包：里面有当前客户端的角色记录（含 SpawnPosition）。
            // 让世界传输丢弃旧缓存；下次导出前会执行 SuMultiplayerPlayers.xml.SaveIfDirty()。
            m_worldInvalidator?.InvalidateWorldSnapshot("新客户端加入：刷新角色记录/复活点快照");
            if (!CaptureHostTable(out string reason))
            {
                NoteProblem("新客户端加入时抓复活点表失败（" + reason + "）→ 本次不发 full，等下一拍");
                return;
            }
            ApplyCurrentHostTable();
            m_pendingFullSnapshotClients.Add(clientId);
            TrySendFullSnapshotTo(clientId, rejoin ? "重连" : "加入");
        }

        private void HandleClientLeft(int clientId, string reason)
        {
            if (!m_attached || m_broadcast == null || !m_broadcast.IsHosting) return;
            m_pendingFullSnapshotClients.Remove(clientId);
            m_worldInvalidator?.InvalidateWorldSnapshot("客户端离开：刷新世界快照");
        }

        private void CaptureAndPublishHostTable()
        {
            if (!CaptureHostTable(out string failure))
            {
                NoteProblem("抓主机复活点表失败（" + (failure ?? "未知") +
                    "）→ 本拍不广播，避免把在线角色误判成离线");
                return;
            }
            if (m_pendingFullSnapshotClients.Count > 0)
            {
                ApplyCurrentHostTable();
                foreach (int pendingClientId in new List<int>(m_pendingFullSnapshotClients))
                    TrySendFullSnapshotTo(pendingClientId, "全量快照重试");
            }
            if (!m_hostSnapshotBroadcast && m_broadcast != null && m_broadcast.PlayerPeerCount > 0)
            {
                // 初始化全量广播必须先让 m_hostEntries 指向本拍抓到的表；否则空表会被当成
                // “所有角色都没有复活点”发出去（新客户端 full 路径已单独 ApplyCurrentHostTable）。
                ApplyCurrentHostTable();
                int sent = m_broadcast.BroadcastToPlayers(Proto.MessageType.PlayerSpawn,
                    Encode(full: true, m_hostRevision, null));
                if (sent > 0)
                {
                    m_hostSnapshotBroadcast = true;
                    m_hostSnapshotsSent += sent;
                    m_hostEntriesSent += m_hostEntries.Count;
                }
            }
            var changed = new List<Proto.PlayerSpawnEntry>();
            foreach (KeyValuePair<PlayerKey, HostEntry> pair in m_hostEntries)
            {
                if (!m_currentHostTable.TryGetValue(pair.Key, out HostEntry current))
                {
                    changed.Add(BuildEntry(pair.Key, Vector3.Zero, string.Empty, false));
                    continue;
                }
                if (!Near(pair.Value.Spawn, current.Spawn) ||
                    !string.Equals(pair.Value.Identity, current.Identity, StringComparison.Ordinal))
                {
                    changed.Add(BuildEntry(pair.Key, current.Spawn, current.Identity, true));
                }
            }
            foreach (KeyValuePair<PlayerKey, HostEntry> pair in m_currentHostTable)
            {
                if (!m_hostEntries.ContainsKey(pair.Key))
                    changed.Add(BuildEntry(pair.Key, pair.Value.Spawn, pair.Value.Identity, true));
            }
            ApplyCurrentHostTable();
            if (changed.Count == 0 || m_broadcast == null) return;
            // 角色记录/复活点变了：下一个中途加入客户端下载的世界包必须重新导出，
            // 否则缓存 pak 带的是旧 SuMultiplayerPlayers.xml（其他端旧复活点）。
            m_worldInvalidator?.InvalidateWorldSnapshot("在线角色复活点/角色集合变化");
            m_hostRevision++;
            int sentCount = m_broadcast.BroadcastToPlayers(Proto.MessageType.PlayerSpawn,
                Encode(full: false, m_hostRevision, changed));
            if (sentCount > 0)
            {
                m_hostDeltasSent++;
                m_hostEntriesSent += changed.Count;
            }
        }

        private void ApplyCurrentHostTable()
        {
            m_hostEntries.Clear();
            foreach (KeyValuePair<PlayerKey, HostEntry> pair in m_currentHostTable)
                m_hostEntries[pair.Key] = pair.Value;
        }

        private bool CaptureHostTable(out string failure)
        {
            failure = null;
            m_currentHostTable.Clear();
            if (m_provider == null)
            {
                failure = "没有复活点来源端口";
                return false;
            }
            var entries = new List<OnlineSpawnPoint>();
            int count = m_provider.CaptureOnlineSpawnPoints(entries, out string reason);
            if (count < 0)
            {
                failure = reason ?? "世界/角色暂不可用";
                return false;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                OnlineSpawnPoint entry = entries[i];
                if (!entry.Key.IsValid || !IsFinite(entry.SpawnPosition) ||
                    entry.SpawnPosition == Vector3.Zero)
                    continue;
                m_currentHostTable[entry.Key] = new HostEntry
                {
                    Spawn = entry.SpawnPosition,
                    Identity = entry.Identity ?? string.Empty
                };
            }
            return true;
        }

        private bool TrySendFullSnapshotTo(int clientId, string why)
        {
            if (m_transport == null) return false;
            byte[] payload = Encode(full: true, m_hostRevision, null);
            if (!m_transport.TrySendTo(clientId, Proto.MessageType.PlayerSpawn, payload))
            {
                NoteProblem("给 clientId=" + clientId + " 发复活点全量快照没有入队（" + why +
                    "）→ 保留在待发集合，下一拍重试（不会当成成功）");
                return false;
            }
            m_pendingFullSnapshotClients.Remove(clientId);
            m_hostSnapshotsSent++;
            m_hostEntriesSent += m_hostEntries.Count;
            Info("已给 clientId=" + clientId + " 发复活点全量快照（" + why +
                " · " + m_hostEntries.Count + " 条）");
            return true;
        }

        private static Proto.PlayerSpawnEntry BuildEntry(PlayerKey key, Vector3 spawn,
            string identity, bool online) =>
            new Proto.PlayerSpawnEntry
            {
                ClientId = key.ClientId,
                Slot = (uint)key.Slot,
                Online = online,
                X = spawn.X,
                Y = spawn.Y,
                Z = spawn.Z,
                Identity = identity ?? string.Empty
            };

        private byte[] Encode(bool full, uint revision, List<Proto.PlayerSpawnEntry> delta)
        {
            var state = new Proto.PlayerSpawnState
            {
                Full = full,
                Revision = revision
            };
            if (delta != null)
            {
                state.Entries.AddRange(delta);
            }
            else
            {
                foreach (KeyValuePair<PlayerKey, HostEntry> pair in m_hostEntries)
                    state.Entries.Add(BuildEntry(pair.Key, pair.Value.Spawn, pair.Value.Identity, true));
            }
            return state.ToByteArray();
        }

        // ---------------------------------------------------------------- 客户端侧

        private void RefreshClientSession()
        {
            int localId = m_transport?.LocalClientId ?? int.MinValue;
            if (localId == m_lastLocalClientId) return;
            m_lastLocalClientId = localId;
            // 注意：**不能在这里清空 m_clientEntries/m_clientPending**。
            // 主机的 full 快照是在 ClientJoined 时发的，它可能比客户端 Tick 看到新的
            // LocalClientId 更早到达；如果这里清空，就会把刚收到的“其他端复活点”整表丢掉
            // （实机：C1 端远端 Geoffrey/Zachary 的 SpawnPosition 一直停在 GlobalSpawn）。
            // 会话切换的旧表由下一份 full 快照覆盖；断线时下面单独清空。
            m_clientRevision = 0;
            if (localId < 0)
            {
                m_clientEntries.Clear();
                m_clientPending.Clear();
            }
            else
            {
                Info("客户端复活点表会话切换：本机通道编号=" + localId +
                    "（保留已收到的表项，等主机 full 覆盖；不再丢弃早到的快照）");
            }
        }

        private void OnPlayerSpawn(MessageContext context, Proto.PlayerSpawnState message)
        {
            if (!m_attached || message == null) return;
            if (m_broadcast != null && m_broadcast.IsHosting)
            {
                NoteProblem("主机收到反向 PLAYER_SPAWN（来源 clientId=" + context.ClientId +
                    "）→ 丢弃（复活点只能主机→客户端）");
                return;
            }
            if (message.Full)
            {
                m_clientEntries.Clear();
                m_clientPending.Clear();
                m_clientRevision = message.Revision;
                m_clientSnapshotsReceived++;
            }
            else
            {
                if (message.Revision <= m_clientRevision && !(m_clientRevision == 0 && message.Revision == 0))
                {
                    m_clientStaleDropped++;
                    return;
                }
                m_clientRevision = message.Revision;
                m_clientDeltasReceived++;
            }
            for (int i = 0; i < message.Entries.Count; i++)
            {
                Proto.PlayerSpawnEntry entry = message.Entries[i];
                if (!PlayerKey.TryCreate(entry.ClientId, (int)entry.Slot, out PlayerKey key,
                        out string keyReason))
                {
                    NoteProblem("PLAYER_SPAWN 条目玩家键非法（" + keyReason + "）→ 跳过");
                    continue;
                }
                if (!entry.Online)
                {
                    m_clientEntries.Remove(key);
                    m_clientPending.Remove(key);
                    continue;
                }
                var spawn = new Vector3(entry.X, entry.Y, entry.Z);
                if (!IsFinite(spawn) || spawn == Vector3.Zero)
                {
                    NoteProblem("PLAYER_SPAWN 条目 " + key + " 的复活点非法/为零 → 跳过");
                    continue;
                }
                m_clientEntries[key] = spawn;
                m_clientPending.Add(key);
            }
            ApplyPendingClientEntries();
        }

        private void ApplyPendingClientEntries()
        {
            if (m_clientPending.Count == 0) return;
            List<PlayerKey> keys = new List<PlayerKey>(m_clientPending);
            for (int i = 0; i < keys.Count; i++)
            {
                PlayerKey key = keys[i];
                if (!m_clientEntries.TryGetValue(key, out Vector3 spawn)) continue;
                if (m_provider.TryApplySpawnPoint(key, spawn, out string reason))
                {
                    m_clientPending.Remove(key);
                    m_clientEntriesApplied++;
                }
                else
                {
                    m_applyFailures++;
                    NoteProblem("复活点暂不能应用到 " + key + "（" + (reason ?? "未知") +
                        "）→ 保留 pending，角色建好后重试");
                }
            }
        }

        // ---------------------------------------------------------------- 工具

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);

        private static bool Near(Vector3 a, Vector3 b) =>
            Math.Abs(a.X - b.X) < 0.001f && Math.Abs(a.Y - b.Y) < 0.001f &&
            Math.Abs(a.Z - b.Z) < 0.001f;

        private void NoteProblem(string message)
        {
            long nowMs = (long)(m_clock() * 1000.0);
            string key = message.Length > 96 ? message.Substring(0, 96) : message;
            if (m_lastProblemMs.TryGetValue(key, out long last) &&
                nowMs - last < ProblemLogIntervalMs) return;
            m_lastProblemMs[key] = nowMs;
            Warn(message);
        }

        private void Error(string message) =>
            m_context?.Diagnostics?.Error(ModuleIdText, message, null);

        private void Warn(string message) =>
            m_context?.Diagnostics?.Warn(ModuleIdText, message);

        private void Info(string message) =>
            m_context?.Diagnostics?.Info(ModuleIdText, message);
    }
}

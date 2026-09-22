using System;
using System.Collections.Generic;
using Engine;
using Game;
using GameEntitySystem;

namespace ScMultiplayer
{
    /// <summary>
    /// 主机端「掉线重连宽限」（Away）。
    ///
    /// 客户端本身早就会自动重连（`UpdateHostReconnect`：5 次尝试、1→5 秒指数退避、握手超时 12 秒，
    /// 总预算约 30–40 秒）。但主机过去把"断线"直接当"离开"处理：立刻存档 + 移除化身，
    /// 客户端重进时只能**重新创建**角色。这里把离开处理按重连窗口推迟：
    ///
    ///   · 断线 → 记一条 Away（记录键 + 原 clientId + 到期时间），**什么都不拆**：
    ///     化身原地保留、不接收输入（客户端已断，本来也没有输入）、不做 AI 移动；
    ///   · 宽限期内重进 → `CancelAway` 命中同一条记录键：先按老路径拆掉那个化身，
    ///     再走正常加入流程（角色属性/位置/背包从记录恢复），等于"没有离开过"；
    ///   · 宽限期满 → `UpdateAwayPlayers` 执行原本的离开处理（存档 + 移除 + 释放角色序号）。
    ///
    /// 记录键是账号 userid（`m_clientRecordKeys`），不是 clientId：重连会换 clientId。
    /// 未登录身份（`network:<clientId>`）每次都不一样，因此不享受宽限，行为与以前一致。
    /// </summary>
    public partial class ScMultiplayer
    {
        private sealed class AwayPlayer
        {
            public int ClientId;
            public string RecordKey = string.Empty;
            public double ExpiresAt;
        }

        private readonly Dictionary<string, AwayPlayer> m_awayPlayers =
            new Dictionary<string, AwayPlayer>(StringComparer.Ordinal);
        private readonly List<string> m_expiredAwayKeys = new List<string>();

        /// <summary>
        /// 主机侧断线入口：能识别身份且宽限开启时，改为"挂起"而不是立刻离开。
        /// 返回 true 表示已经挂起，调用方不要再执行即时的移除。
        /// </summary>
        private bool TryBeginAway(int departedClientId)
        {
            if (!IsHost || ScMultiplayerSettings.RejoinGracePeriodSeconds <= 0.0 || GameManager.Project == null ||
                departedClientId <= 0)
            {
                return false;
            }
            if (!m_clientRecordKeys.TryGetValue(departedClientId, out string recordKey) ||
                string.IsNullOrWhiteSpace(recordKey) ||
                recordKey.StartsWith("network:", StringComparison.Ordinal))
            {
                return false;
            }
            double now = Time.RealTime;
            m_awayPlayers[recordKey] = new AwayPlayer
            {
                ClientId = departedClientId,
                RecordKey = recordKey,
                ExpiresAt = now + ScMultiplayerSettings.RejoinGracePeriodSeconds
            };
            m_clientRecordKeys.Remove(departedClientId);
            Log.Information("[ScMP] Client " + departedClientId + " (" + recordKey +
                ") dropped; holding the player for " + ScMultiplayerSettings.RejoinGracePeriodSeconds.ToString("0") +
                "s in case it reconnects");
            return true;
        }

        /// <summary>
        /// 重连加入时调用：命中挂起的记录键就把它交还给新的 clientId（返回 true）。
        /// 旧化身的拆除与角色序号释放由调用方在此之后立即完成，避免出现两个化身。
        /// </summary>
        private bool TryCancelAway(string recordKey, out int heldClientId)
        {
            heldClientId = -1;
            if (string.IsNullOrWhiteSpace(recordKey) || m_awayPlayers.Count == 0)
                return false;
            if (!m_awayPlayers.TryGetValue(recordKey, out AwayPlayer away) || away == null)
                return false;
            m_awayPlayers.Remove(recordKey);
            heldClientId = away.ClientId;
            Log.Information("[ScMP] Client reconnected (" + recordKey + "), player was held as " +
                away.ClientId + "; reusing the held role");
            return true;
        }

        /// <summary>挂起化身的拆除：与断线时的即时清理保持一致（线路、化身、角色序号）。</summary>
        private void CompleteAwayRemoval(int clientId)
        {
            if (clientId <= 0)
                return;
            try
            {
                m_circuitSynchronizer?.NotifyClientDeparted(clientId);
                RemoveNetworkPlayer(clientId);
                playerMappingManager.ReleasePlayerIndex(clientId);
            }
            catch (Exception ex)
            {
                Log.Error("[ScMP] Failed to release the held player " + clientId + ": " +
                    ex.Message);
            }
        }

        /// <summary>每帧推进：宽限期满就按老路径离开；世界卸载时清空挂起表。</summary>
        private void UpdateAwayPlayers()
        {
            if (m_awayPlayers.Count == 0)
                return;
            if (!IsHost || GameManager.Project == null)
            {
                m_awayPlayers.Clear();
                return;
            }
            double now = Time.RealTime;
            m_expiredAwayKeys.Clear();
            foreach (KeyValuePair<string, AwayPlayer> item in m_awayPlayers)
            {
                if (item.Value == null || now >= item.Value.ExpiresAt)
                    m_expiredAwayKeys.Add(item.Key);
            }
            if (m_expiredAwayKeys.Count == 0)
                return;
            foreach (string key in m_expiredAwayKeys)
            {
                if (!m_awayPlayers.TryGetValue(key, out AwayPlayer away) || away == null)
                    continue;
                m_awayPlayers.Remove(key);
                Log.Information("[ScMP] Rejoin grace expired for " + key + "; player " +
                    away.ClientId + " leaves");
                PublishServerAudit("connection.leave", away.ClientId, "reason=rejoin_grace_expired");
                CompleteAwayRemoval(away.ClientId);
            }
            m_expiredAwayKeys.Clear();
        }
    }
}

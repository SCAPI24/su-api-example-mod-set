using Engine;
using System;

namespace ScMultiplayer
{
    /// <summary>
    /// 《玩家领地》P2：领地数据的**下发与增量同步**（设计稿 §1「同步」）。
    ///
    ///   · 主机 → 客户端：加入完成时整表下发（`Full`）；运行期每次改动广播一处增量（`Delta`）；
    ///   · 报文带**单调序号**，客户端按序号应用：`<=` 已应用序号视为旧包丢弃，
    ///     `== 已应用 + 1` 才落地，跳号则回 `RequestSync` 让主机补发整表；
    ///   · 客户端在加入流程结束时主动请求一次整表（覆盖"主机先发、客户端后进世界"的时序）。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs（主机广播写法）
    /// Source: Mod/ScMultiplayer/Modules/Join/ScMultiplayerWorldTransferHandlers.cs（加入完成挂点）
    /// </summary>
    public partial class ScMultiplayer
    {
        private bool m_clientRegionClaimResyncRequested;

        /// <summary>主机侧：广播一处领地改动（可靠有序；加入中的客户端会被 catch-up 日志一并带走）。</summary>
        private void BroadcastRegionClaimChange(RegionClaim claim, RegionClaimOperation operation)
        {
            if (!IsHost || client == null || claim == null)
                return;
            RegionClaimMessage message = operation == RegionClaimOperation.Remove
                ? RegionClaimMessage.CreateDelta(m_regionClaimSequence, m_regionClaimNextId,
                    operation, null, claim.Id)
                : RegionClaimMessage.CreateDelta(m_regionClaimSequence, m_regionClaimNextId,
                    operation, claim.Clone(), 0);
            NetworkMessageSender.SendScheduledMessage(-1, message, sequenced: true);
            // ⚠️ 不要用 operation.ToString()：枚举成员也会被 Obfuscar 改名（实测日志里操作名成了空串）。
            string operationName = operation == RegionClaimOperation.Remove ? "remove" :
                operation == RegionClaimOperation.Replace ? "replace" : "add";
            PublishServerAudit("region." + operationName, -1,
                "id=" + claim.Id + " sequence=" + m_regionClaimSequence +
                " count=" + m_regionClaims.Count);
            Log.Information("[ScMP] Region claim " + operationName + ": #" + claim.Id +
                ", sequence=" + m_regionClaimSequence + ", count=" + m_regionClaims.Count);
        }

        /// <summary>主机侧：拥有者变化（赋予 / 剥夺）→ 广播 Replace 增量。</summary>
        private void PublishRegionClaimReplace(RegionClaim claim)
        {
            BroadcastRegionClaimChange(claim, RegionClaimOperation.Replace);
        }

        /// <summary>主机侧：把整表发给某个客户端（加入完成 / 客户端请求补发）。</summary>
        private void SendFullRegionClaimsToClient(int targetClientId)
        {
            if (!IsHost || targetClientId <= 0 || client == null)
                return;
            RegionClaimMessage message = RegionClaimMessage.CreateFull(m_regionClaimSequence,
                m_regionClaimNextId, m_regionClaims);
            NetworkMessageSender.SendScheduledMessage(targetClientId, message, sequenced: true);
            PublishServerAudit("region.sync", targetClientId,
                "count=" + m_regionClaims.Count + " sequence=" + m_regionClaimSequence);
            Log.Information("[ScMP] Region claims sent to ClientID=" + targetClientId +
                ": count=" + m_regionClaims.Count + ", sequence=" + m_regionClaimSequence);
        }

        /// <summary>客户端：请求主机补发整表（序号缺口，或加入流程刚结束）。</summary>
        private void RequestRegionClaimResync(bool force)
        {
            if (IsHost || client == null)
                return;
            if (m_clientRegionClaimResyncRequested && !force)
                return;
            m_clientRegionClaimResyncRequested = true;
            NetworkMessageSender.SendRegionClaimRequestSync();
        }

        private void HandleRegionClaimMessage(RegionClaimMessage message, int sourceClientId)
        {
            if (message == null)
                return;
            if (IsHost)
            {
                if (message.Stage == RegionClaimStage.RequestSync && sourceClientId > 0)
                    SendFullRegionClaimsToClient(sourceClientId);
                return;
            }
            // 客户端只认主机（ClientID 0）下发的领地数据。
            if (sourceClientId != 0 || message.Stage == RegionClaimStage.RequestSync)
                return;
            if (message.Stage == RegionClaimStage.Full)
            {
                ApplyRegionClaimSnapshot(message.Claims, message.NextId, message.Sequence);
                m_clientRegionClaimResyncRequested = false;
                Log.Information("[ScMP] Region claims synced from host: count=" +
                    m_regionClaims.Count + ", sequence=" + m_regionClaimSequence +
                    ", nextId=" + m_regionClaimNextId);
                return;
            }
            if (message.Sequence <= m_regionClaimSequence)
                return;
            if (message.Sequence != m_regionClaimSequence + 1L)
            {
                Log.Warning("[ScMP] Region claim sequence gap: applied=" +
                    m_regionClaimSequence + ", received=" + message.Sequence);
                RequestRegionClaimResync(false);
                return;
            }
            RegionClaim claim = message.Claims != null && message.Claims.Count > 0
                ? message.Claims[0]
                : null;
            ApplyRegionClaimDelta(claim, message.RemoveId, message.Operation, message.NextId,
                message.Sequence);
            m_clientRegionClaimResyncRequested = false;
            string deltaName = message.Operation == RegionClaimOperation.Remove ? "remove" :
                message.Operation == RegionClaimOperation.Replace ? "replace" : "add";
            Log.Information("[ScMP] Region claim replica updated from host: " + deltaName +
                " #" + (claim != null ? claim.Id : message.RemoveId) +
                ", sequence=" + m_regionClaimSequence + ", count=" + m_regionClaims.Count);
        }

        /// <summary>客户端加入流程结束：主动要一次整表（幂等，主机回 Full 就清标志）。</summary>
        private void RequestRegionClaimSyncAfterJoin()
        {
            if (IsHost)
                return;
            RequestRegionClaimResync(true);
        }
    }
}

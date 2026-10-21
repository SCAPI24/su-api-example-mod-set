using System;
using System.Collections.Generic;
using System.Text;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 自定义皮肤链路的**唯一计数表**（用户口径逐项）：
    ///   skinAssetSent / Recv / Applied / Rejected(reason) / Malformed / Oversize /
    ///   HashMismatch / PlaceholderShown。
    ///
    /// 产品口径 2026-10-21（主机接受未知皮肤 / 资源只转发给其他端 / 发起者只收回执）新增：
    ///   skinUploadAccepted / skinUploadRejected(原因) / skinAssetForwarded /
    ///   skinForwardExcludedOrigin / skinAckSent / skinAckFailures / skinTooLarge(原因) /
    ///   skinChangeWithoutAsset（收到换肤通知但主机还没有资源 → 不拒绝、不回 MISSING）。
    ///
    /// 额外计数只用于把"明确失败"说清楚（发送失败 / 记录缺资源 / 去重 / 变更被拒），
    /// 不替代上面 8 项的语义。日志由模块按 10 秒限流输出 <see cref="Describe"/>。
    ///
    /// 本类只被主线程使用（消息派发与模块 Tick 都在主线程），因此不做跨线程同步；
    /// 自检也直接读属性断言。
    /// </summary>
    public sealed class SkinAssetCounters
    {
        private readonly Dictionary<string, long> m_reasons =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<string> m_reasonOrder = new List<string>();

        public long Sent { get; private set; }
        public long Received { get; private set; }
        public long Applied { get; private set; }
        public long Rejected { get; private set; }
        public long Malformed { get; private set; }
        public long Oversize { get; private set; }
        public long HashMismatch { get; private set; }
        public long PlaceholderShown { get; private set; }
        /// <summary>主机把资源发给某个玩家端失败（通道不可用 / 背压 / 对端已断）。</summary>
        public long SendFailed { get; private set; }
        /// <summary>主机没有该哈希的资源（记录里也没有），只能让其他端显示占位。</summary>
        public long Missing { get; private set; }
        /// <summary>收到的资源与缓存完全一致（去重，不重复应用）。</summary>
        public long Deduped { get; private set; }
        /// <summary>主机拒绝了客户端的中途换肤请求（客户端会回退到主机确认过的资料）。</summary>
        public long ChangeRejected { get; private set; }
        /// <summary>换肤请求因本机没有资源 / 无法上传而明确失败。</summary>
        public long ChangeFailed { get; private set; }

        // ---- 产品口径 2026-10-21：主机接受未知皮肤 / 资源只转发给其他端 / 发起者只收回执 ----

        /// <summary>主机完整接收并接受了一次客户端皮肤上传（新资源与重复哈希都算接受）。</summary>
        public long UploadAccepted { get; private set; }
        /// <summary>主机拒绝了一次客户端皮肤上传（原因进 reasons=upload-rejected:...）。</summary>
        public long UploadRejected { get; private set; }
        /// <summary>皮肤资源分片成功入队给玩家的次数（分片 × 目标端；不含发起者）。</summary>
        public long AssetForwarded { get; private set; }
        /// <summary>转发资源时**排除了发起者**的次数（"资源不回传给换皮肤的人"）。</summary>
        public long ForwardExcludedOrigin { get; private set; }
        /// <summary>主机给发起者发出的一条 accepted / rejected 回执（ITransportPort.TrySendTo 单发）。</summary>
        public long AckSent { get; private set; }
        /// <summary>回执发送失败（通道不可用 / 背压 / 没有目标）。</summary>
        public long AckFailures { get; private set; }
        /// <summary>因超过 SkinAssetPolicy 上限被拒绝（单资产 / 分片 / 压缩后；原因进 reasons=too-large:...）。</summary>
        public long TooLarge { get; private set; }
        /// <summary>主机收到"换成某自定义皮肤"的通知，但此刻没有该资源（不伪造、不回 MISSING，等上传路径）。</summary>
        public long ChangeWithoutAsset { get; private set; }
        /// <summary>
        /// 主机加入补发时因"对端 Hello 申报的角色记录 hash 与当前一致"而跳过重发的资源条数
        /// （重连 diff 的省流量部分；跳过的都是对端本地 pak 副本里已有的不常变内容）。
        /// </summary>
        public long BackfillSkippedUnchanged { get; private set; }

        /// <summary>计数器自上次日志以来的总变化量（模块据此决定是否写一行）。</summary>
        public long Total =>
            Sent + Received + Applied + Rejected + PlaceholderShown + SendFailed + Missing +
            Deduped + ChangeRejected + ChangeFailed + UploadAccepted + UploadRejected +
            AssetForwarded + ForwardExcludedOrigin + AckSent + AckFailures + TooLarge +
            ChangeWithoutAsset + BackfillSkippedUnchanged;

        public IReadOnlyDictionary<string, long> Reasons => m_reasons;

        public void OnSent() => Sent++;

        public void OnReceived() => Received++;

        public void OnApplied() => Applied++;

        public void OnPlaceholderShown() => PlaceholderShown++;

        public void OnDeduped() => Deduped++;

        public void OnSendFailed(string reason)
        {
            SendFailed++;
            AddReason("send:" + Normalize(reason));
        }

        public void OnMissing(string reason)
        {
            Missing++;
            AddReason("missing:" + Normalize(reason));
        }

        public void OnChangeRejected(string reason)
        {
            ChangeRejected++;
            AddReason("change:" + Normalize(reason));
        }

        public void OnChangeFailed(string reason)
        {
            ChangeFailed++;
            AddReason("change-failed:" + Normalize(reason));
        }

        /// <summary>主机完整接收并接受了一次客户端上传（新资源 / 重复哈希都算）。</summary>
        public void OnUploadAccepted() => UploadAccepted++;

        /// <summary>主机拒绝了客户端的一次上传（明确原因；同时计入 Rejected 分类）。</summary>
        public void OnUploadRejected(string reason)
        {
            UploadRejected++;
            AddReason("upload-rejected:" + Normalize(reason));
        }

        /// <summary>资源分片成功入队给玩家端的次数（分片 × 目标端）。</summary>
        public void OnAssetForwarded(int chunkQueues)
        {
            if (chunkQueues <= 0) return;
            AssetForwarded += chunkQueues;
        }

        /// <summary>这次转发排除了发起者（资源绝不回传给换皮肤的那个客户端）。</summary>
        public void OnForwardExcludedOrigin() => ForwardExcludedOrigin++;

        /// <summary>给发起者发出的一条明确回执（accepted / rejected）。</summary>
        public void OnAckSent() => AckSent++;

        /// <summary>回执发送失败：显式计数 + 原因（调用方不能假装发起者已收到）。</summary>
        public void OnAckFailure(string reason)
        {
            AckFailures++;
            AddReason("ack:" + Normalize(reason));
        }

        /// <summary>超过 SkinAssetPolicy 上限（单资产 / 分片 / 压缩后）。</summary>
        public void OnTooLarge(string reason)
        {
            TooLarge++;
            AddReason("too-large:" + Normalize(reason));
        }

        /// <summary>主机收到某自定义皮肤的换肤通知，但此刻没有资源（不伪造记录 / 不回 MISSING）。</summary>
        public void OnChangeWithoutAsset(string reason)
        {
            ChangeWithoutAsset++;
            AddReason("change-no-asset:" + Normalize(reason));
        }

        /// <summary>主机加入补发时因对端已有同版本记录而跳过一条资源（重连 diff 省流量）。</summary>
        public void OnBackfillSkippedUnchanged() => BackfillSkippedUnchanged++;

        /// <summary>记录一次拒绝：Rejected 总数 + 具体分类 + 原因明细（原因不静默）。</summary>
        public void OnRejected(SkinAssetRejectReason kind, string reason)
        {
            Rejected++;
            switch (kind)
            {
                case SkinAssetRejectReason.Oversize:
                    Oversize++;
                    break;
                case SkinAssetRejectReason.Malformed:
                    Malformed++;
                    break;
                case SkinAssetRejectReason.HashMismatch:
                    HashMismatch++;
                    break;
            }
            AddReason(KindName(kind) + ":" + Normalize(reason));
        }

        public static string KindName(SkinAssetRejectReason kind)
        {
            switch (kind)
            {
                case SkinAssetRejectReason.Oversize: return "oversize";
                case SkinAssetRejectReason.Malformed: return "malformed";
                case SkinAssetRejectReason.HashMismatch: return "hash-mismatch";
                case SkinAssetRejectReason.InvalidName: return "invalid-name";
                case SkinAssetRejectReason.UnknownClient: return "unknown-client";
                case SkinAssetRejectReason.StoreFailed: return "store-failed";
                case SkinAssetRejectReason.NotAllowed: return "not-allowed";
                case SkinAssetRejectReason.TransferConflict: return "transfer-conflict";
                case SkinAssetRejectReason.SendFailed: return "send-failed";
                default: return "none";
            }
        }

        public string Describe()
        {
            var text = new StringBuilder(256);
            text.Append("skinAssetSent=").Append(Sent);
            text.Append(" skinAssetRecv=").Append(Received);
            text.Append(" skinAssetApplied=").Append(Applied);
            text.Append(" skinAssetRejected=").Append(Rejected);
            text.Append(" skinMalformed=").Append(Malformed);
            text.Append(" skinOversize=").Append(Oversize);
            text.Append(" skinHashMismatch=").Append(HashMismatch);
            text.Append(" skinPlaceholderShown=").Append(PlaceholderShown);
            text.Append(" skinAssetSendFailed=").Append(SendFailed);
            text.Append(" skinAssetMissing=").Append(Missing);
            text.Append(" skinAssetDeduped=").Append(Deduped);
            text.Append(" skinChangeRejected=").Append(ChangeRejected);
            text.Append(" skinChangeFailed=").Append(ChangeFailed);
            text.Append(" skinUploadAccepted=").Append(UploadAccepted);
            text.Append(" skinUploadRejected=").Append(UploadRejected);
            text.Append(" skinAssetForwarded=").Append(AssetForwarded);
            text.Append(" skinForwardExcludedOrigin=").Append(ForwardExcludedOrigin);
            text.Append(" skinAckSent=").Append(AckSent);
            text.Append(" skinAckFailures=").Append(AckFailures);
            text.Append(" skinTooLarge=").Append(TooLarge);
            text.Append(" skinChangeWithoutAsset=").Append(ChangeWithoutAsset);
            if (m_reasonOrder.Count > 0)
            {
                text.Append(" reasons=");
                for (int i = 0; i < m_reasonOrder.Count; i++)
                {
                    if (i > 0) text.Append(',');
                    string key = m_reasonOrder[i];
                    text.Append(key).Append(':').Append(m_reasons[key]);
                }
            }
            return text.ToString();
        }

        public void ResetReasonDetails()
        {
            m_reasons.Clear();
            m_reasonOrder.Clear();
        }

        private void AddReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            if (!m_reasons.ContainsKey(reason))
            {
                m_reasons[reason] = 0;
                m_reasonOrder.Add(reason);
            }
            m_reasons[reason] = m_reasons[reason] + 1;
        }

        private static string Normalize(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "(no-reason)";
            string text = reason.Trim().Replace('\r', ' ').Replace('\n', ' ');
            return text.Length <= 120 ? text : text.Substring(0, 120) + "...";
        }
    }
}

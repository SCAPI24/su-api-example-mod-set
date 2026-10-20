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

        /// <summary>计数器自上次日志以来的总变化量（模块据此决定是否写一行）。</summary>
        public long Total =>
            Sent + Received + Applied + Rejected + PlaceholderShown + SendFailed + Missing +
            Deduped + ChangeRejected + ChangeFailed;

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

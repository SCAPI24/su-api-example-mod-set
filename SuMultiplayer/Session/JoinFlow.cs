using System;
using System.Collections.Generic;

namespace SuMultiplayer.Session
{
    /// <summary>加入流程阶段。演示页标记：JOIN01。</summary>
    public enum JoinStage : byte
    {
        Idle = 0,
        Discovering = 1,
        Connecting = 2,
        Requesting = 3,
        AwaitingApproval = 4,
        Downloading = 5,
        Importing = 6,
        AwaitingBarrier = 7,
        Ready = 8,
        Failed = 9
    }

    public enum JoinAbortReason : byte
    {
        None = 0,
        ConnectTimeout = 1,
        ApprovalTimeout = 2,
        ApprovalRejected = 3,
        TransferTimeout = 4,
        TransferFailed = 5,
        ImportFailed = 6,
        BarrierTimeout = 7,
        ProtocolIncompatible = 8,
        UserCancelled = 9,
        HostGone = 10
    }

    /// <summary>
    /// 加入流程状态机（纯逻辑，时钟由调用方注入）。
    /// 时间常量取自旧实现：连接 8s / 审批握手 5min / 等待世界 30s / 传输空闲 15s。
    /// 演示页标记：JOIN02。
    /// </summary>
    public sealed class JoinFlow
    {
        public int ConnectTimeoutMs { get; set; } = 8000;
        public int ApprovalTimeoutMs { get; set; } = 5 * 60 * 1000;
        public int WorldWaitTimeoutMs { get; set; } = 30000;
        public int TransferIdleTimeoutMs { get; set; } = 15000;
        public int BarrierTimeoutMs { get; set; } = 30000;
        public int MaxAttempts { get; set; } = 5;

        private long m_stageSinceMs;
        private long m_lastProgressMs;
        private long m_startedMs;

        public JoinStage Stage { get; private set; } = JoinStage.Idle;
        public JoinAbortReason AbortReason { get; private set; }
        public string Detail { get; private set; } = string.Empty;
        public int Attempt { get; private set; }
        public string TargetKey { get; private set; }
        public int LastRejectedCount { get; private set; }
        public int StageTimeoutCount { get; private set; }

        public bool IsActive => Stage != JoinStage.Idle && Stage != JoinStage.Ready && Stage != JoinStage.Failed;

        public long ElapsedMs(long nowMs) => Stage == JoinStage.Idle ? 0 : nowMs - m_startedMs;

        public event Action<JoinStage, JoinStage, string> StageChanged;

        private void Move(JoinStage stage, long nowMs, string detail)
        {
            if (Stage == stage && Detail == detail) return;
            JoinStage previous = Stage;
            Stage = stage;
            Detail = detail ?? string.Empty;
            m_stageSinceMs = nowMs;
            m_lastProgressMs = nowMs;
            StageChanged?.Invoke(previous, stage, Detail);
        }

        public void Discover(long nowMs)
        {
            m_startedMs = nowMs;
            Attempt = 1;
            Move(JoinStage.Discovering, nowMs, "正在扫描局域网与服务器目录");
        }

        public void FoundRooms(int count, long nowMs)
        {
            if (Stage != JoinStage.Discovering) return;
            Detail = "发现 " + count + " 个房间";
            if (count == 0)
            {
                Fail(JoinAbortReason.UserCancelled, nowMs, "没有可用房间");
            }
        }

        public void BeginConnect(string targetKey, long nowMs)
        {
            TargetKey = targetKey;
            AbortReason = JoinAbortReason.None;
            if (Attempt == 0) Attempt = 1;   // 首次连接算第 1 次尝试
            Move(JoinStage.Connecting, nowMs, "连接 " + targetKey);
        }

        public void ProtocolRejected(string reason, long nowMs) =>
            Fail(JoinAbortReason.ProtocolIncompatible, nowMs, reason);

        public void Connected(long nowMs) => Move(JoinStage.Requesting, nowMs, "已连接，发送加入请求");

        public void RequestSent(long nowMs) => Move(JoinStage.AwaitingApproval, nowMs, "等待主机审批");

        public void ApprovalReceived(bool accepted, string reason, long nowMs)
        {
            if (!accepted)
            {
                LastRejectedCount++;
                Fail(JoinAbortReason.ApprovalRejected, nowMs, reason ?? "被拒绝");
                return;
            }
            Move(JoinStage.Downloading, nowMs, "已批准，开始接收世界");
        }

        public void TransferStarted(int totalChunks, long nowMs) =>
            Move(JoinStage.Downloading, nowMs, "世界分片 0/" + totalChunks);

        public void TransferProgress(int received, int total, long nowMs)
        {
            if (Stage != JoinStage.Downloading) return;
            m_lastProgressMs = nowMs;
            Detail = "世界分片 " + received + "/" + total;
        }

        public void TransferCompleted(long nowMs) => Move(JoinStage.Importing, nowMs, "分片收齐，正在导入世界");

        public void ImportCompleted(long nowMs) =>
            Move(JoinStage.AwaitingBarrier, nowMs, "世界已导入，等待追帧屏障");

        public void BarrierPassed(long nowMs)
        {
            Move(JoinStage.Ready, nowMs, "校准完成，进入游戏");
            TargetKey = null;
        }

        public void Fail(JoinAbortReason reason, long nowMs, string detail)
        {
            AbortReason = reason;
            Move(JoinStage.Failed, nowMs, detail ?? reason.ToString());
        }

        public void Reset(long nowMs)
        {
            Stage = JoinStage.Idle;
            AbortReason = JoinAbortReason.None;
            Detail = string.Empty;
            Attempt = 0;
            TargetKey = null;
            m_startedMs = nowMs;
        }

        public bool CanRetry => Attempt < MaxAttempts &&
            (AbortReason == JoinAbortReason.ConnectTimeout || AbortReason == JoinAbortReason.TransferTimeout ||
             AbortReason == JoinAbortReason.HostGone || AbortReason == JoinAbortReason.BarrierTimeout);

        /// <summary>推进超时判定。返回 true 表示本帧发生了状态变化。</summary>
        public bool Tick(long nowMs)
        {
            if (!IsActive) return false;
            JoinStage before = Stage;
            long inStage = nowMs - m_stageSinceMs;
            switch (Stage)
            {
                case JoinStage.Connecting:
                    if (inStage > ConnectTimeoutMs)
                        Fail(JoinAbortReason.ConnectTimeout, nowMs, "连接超时 " + ConnectTimeoutMs + " ms");
                    break;
                case JoinStage.AwaitingApproval:
                    if (inStage > ApprovalTimeoutMs)
                        Fail(JoinAbortReason.ApprovalTimeout, nowMs, "审批超时");
                    break;
                case JoinStage.Downloading:
                    if (nowMs - m_lastProgressMs > TransferIdleTimeoutMs)
                        Fail(JoinAbortReason.TransferTimeout, nowMs, "世界传输停滞 " + TransferIdleTimeoutMs + " ms");
                    break;
                case JoinStage.AwaitingBarrier:
                    if (inStage > BarrierTimeoutMs)
                        Fail(JoinAbortReason.BarrierTimeout, nowMs, "追帧屏障超时");
                    break;
                case JoinStage.Requesting:
                    if (inStage > WorldWaitTimeoutMs)
                        Fail(JoinAbortReason.ConnectTimeout, nowMs, "请求无响应");
                    break;
            }
            return before != Stage;
        }

        /// <summary>重试：回到 Connecting，Attempt+1（保留目标）。</summary>
        public bool Retry(long nowMs)
        {
            if (!CanRetry) return false;
            Attempt++;
            AbortReason = JoinAbortReason.None;
            Move(JoinStage.Connecting, nowMs, "第 " + Attempt + " 次尝试连接 " + TargetKey);
            return true;
        }
    }
}

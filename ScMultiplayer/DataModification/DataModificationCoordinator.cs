using Engine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Networking/NetworkMessageSender.cs:
    // NetworkMessageSender.SendDataModificationMessage
    // The coordinator owns permission, bounded queues and frame budgets. It never interprets the
    // payload; an installed host-side Mod must perform the authoritative game mutation.
    internal sealed class DataModificationCoordinator : IDisposable
    {
        private const int MaximumFastPayloadBytes = 8192;
        private const int MaximumBulkChunkBytes = 768;
        private const int MaximumBulkBytes = 16 * 1024 * 1024;
        private const int MaximumBulkChunks = 65535;
        private const int MaximumBufferedBulkBytes = 2 * 1024 * 1024;
        private const double BulkTimeoutSeconds = 30.0;
        private const int MaximumQueuedClientSubmissions = 32;
        private const int MaximumFastRequestsPerFrame = 64;
        private const int MaximumPendingApprovals = 32;
        private const double ApprovalTimeoutSeconds = 120.0;

        private readonly ScMultiplayer m_owner;
        private readonly ConcurrentQueue<DataModificationSubmitRequest> m_clientSubmissions =
            new ConcurrentQueue<DataModificationSubmitRequest>();
        private readonly Queue<HostFastRequest> m_hostFastRequests =
            new Queue<HostFastRequest>();
        private readonly HashSet<long> m_pendingFastKeys = new HashSet<long>();
        private readonly Dictionary<long, DataModificationResult> m_fastResultCache =
            new Dictionary<long, DataModificationResult>();
        private readonly Queue<long> m_fastResultOrder = new Queue<long>();
        private readonly Dictionary<long, HostBulkTransfer> m_hostBulkTransfers =
            new Dictionary<long, HostBulkTransfer>();
        private readonly List<HostBulkTransfer> m_hostBulkOrder = new List<HostBulkTransfer>();
        private readonly Dictionary<int, ClientBulkTransfer> m_clientBulkTransfers =
            new Dictionary<int, ClientBulkTransfer>();
        private readonly Dictionary<long, PendingApproval> m_pendingApprovals =
            new Dictionary<long, PendingApproval>();
        private readonly Queue<long> m_pendingApprovalOrder = new Queue<long>();
        private int m_nextRequestId;
        private int m_nextTransferId;
        private int m_hostBulkCursor;
        private bool m_disposed;

        public DataModificationCoordinator(ScMultiplayer owner)
        {
            m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public int PendingFastCount => m_hostFastRequests.Count;

        public int ActiveBulkCount => m_hostBulkOrder.Count;

        public int PendingApprovalCount => m_pendingApprovals.Count;

        public DataModificationSubmitResult Submit(DataModificationSubmitRequest request)
        {
            if (m_disposed)
                return Rejected("Data modification coordinator is unavailable.");
            if (!m_owner.IsInRoom || ScMultiplayer.client?.IsConnected != true)
                return Rejected("A connected multiplayer room is required.");
            if (!TryNormalizeRequest(request, out DataModificationSubmitRequest normalized,
                out string error))
                return Rejected(error);
            if (m_clientSubmissions.Count >= MaximumQueuedClientSubmissions)
                return new DataModificationSubmitResult
                {
                    Code = DataModificationResultCode.Busy,
                    Details = "The client DM queue is full."
                };

            normalized.RequestId = NextRequestId();
            normalized.TransferId = normalized.Channel == DataModificationChannel.Bulk
                ? NextTransferId() : 0;
            m_clientSubmissions.Enqueue(normalized);
            return new DataModificationSubmitResult
            {
                Code = DataModificationResultCode.Accepted,
                RequestId = normalized.RequestId,
                TransferId = normalized.TransferId,
                Details = "Queued for the authoritative host."
            };
        }

        public void Receive(DataModificationMessage message, int sourceClientId)
        {
            if (m_disposed || message == null)
                return;
            if (!m_owner.IsInRoom)
                return;
            if (ScMultiplayer.IsHost)
            {
                if (sourceClientId < 0)
                    return;
                ReceiveOnHost(message, sourceClientId);
            }
            else if (sourceClientId == 0 && message.Stage == DataModificationMessageStage.Result)
            {
                ReceiveOnClient(message);
            }
        }

        public void Update(double now)
        {
            if (m_disposed || !m_owner.IsInRoom || ScMultiplayer.client?.IsConnected != true)
                return;
            if (ScMultiplayer.IsHost)
            {
                ProcessPendingApprovals(now);
                ProcessLocalSubmissions();
                ProcessClientBulkTransfers();
                ProcessHostFastRequests();
                ProcessHostBulkTransfers(now);
                return;
            }

            ProcessClientSubmissions();
            ProcessClientBulkTransfers();
        }

        public void Reset()
        {
            while (m_clientSubmissions.TryDequeue(out _)) { }
            m_hostFastRequests.Clear();
            m_pendingFastKeys.Clear();
            m_fastResultCache.Clear();
            m_fastResultOrder.Clear();
            m_hostBulkTransfers.Clear();
            m_hostBulkOrder.Clear();
            m_clientBulkTransfers.Clear();
            m_pendingApprovals.Clear();
            m_pendingApprovalOrder.Clear();
            m_hostBulkCursor = 0;
        }

        public void Dispose()
        {
            if (m_disposed)
                return;
            m_disposed = true;
            Reset();
        }

        private void ProcessLocalSubmissions()
        {
            int processed = 0;
            while (processed < MaximumFastRequestsPerFrame &&
                m_clientSubmissions.TryDequeue(out DataModificationSubmitRequest request))
            {
                if (request.Channel == DataModificationChannel.Fast)
                {
                    ReceiveOnHost(CreateFastMessage(request), 0);
                }
                else
                {
                    ClientBulkTransfer transfer = CreateClientBulkTransfer(request);
                    transfer.IsLocalHost = true;
                    m_clientBulkTransfers[request.TransferId] = transfer;
                    ReceiveOnHost(transfer.BeginMessage, 0);
                }
                processed++;
            }
        }

        private void ProcessClientSubmissions()
        {
            int processed = 0;
            while (processed < MaximumFastRequestsPerFrame &&
                m_clientSubmissions.TryDequeue(out DataModificationSubmitRequest request))
            {
                if (request.Channel == DataModificationChannel.Fast)
                {
                    NetworkMessageSender.SendDataModificationMessage(0, CreateFastMessage(request));
                }
                else
                {
                    ClientBulkTransfer transfer = CreateClientBulkTransfer(request);
                    m_clientBulkTransfers[request.TransferId] = transfer;
                    NetworkMessageSender.SendDataModificationMessage(0, transfer.BeginMessage);
                }
                processed++;
            }
        }

        private void ProcessClientBulkTransfers()
        {
            int chunks = 0;
            int bytes = 0;
            int chunkBudget = Math.Max(1, ScMultiplayerSettings
                .DataModificationBulkApplyChunksPerFrame);
            int byteBudget = Math.Max(MaximumBulkChunkBytes, ScMultiplayerSettings
                .DataModificationBulkApplyBytesPerFrame);
            foreach (ClientBulkTransfer transfer in
                new List<ClientBulkTransfer>(m_clientBulkTransfers.Values))
            {
                if (!transfer.Accepted || transfer.CompleteSent)
                    continue;
                int allowedChunks = Math.Max(1, Math.Min(chunkBudget,
                    transfer.ServerBulkChunksPerFrame > 0
                        ? transfer.ServerBulkChunksPerFrame : chunkBudget));
                int allowedBytes = Math.Max(MaximumBulkChunkBytes, Math.Min(byteBudget,
                    transfer.ServerBulkBytesPerFrame > 0
                        ? transfer.ServerBulkBytesPerFrame : byteBudget));
                while (transfer.NextChunkIndex < transfer.Batches.Count &&
                    chunks < allowedChunks && bytes < allowedBytes)
                {
                    byte[] payload = transfer.Batches[transfer.NextChunkIndex];
                    if (bytes > 0 && bytes + payload.Length > allowedBytes)
                        break;
                    var chunkMessage = new DataModificationMessage
                    {
                        Stage = DataModificationMessageStage.BulkChunk,
                        Channel = DataModificationChannel.Bulk,
                        RequestId = transfer.RequestId,
                        TransferId = transfer.TransferId,
                        ModId = transfer.ModId,
                        Operation = transfer.Operation,
                        ChunkIndex = transfer.NextChunkIndex,
                        ChunkCount = transfer.Batches.Count,
                        TotalBytes = transfer.TotalBytes,
                        Payload = payload
                    };
                    if (transfer.IsLocalHost)
                        ReceiveOnHost(chunkMessage, 0);
                    else
                        NetworkMessageSender.SendDataModificationMessage(0, chunkMessage);
                    transfer.NextChunkIndex++;
                    chunks++;
                    bytes += payload.Length;
                }
                if (transfer.NextChunkIndex == transfer.Batches.Count)
                {
                    var completeMessage = new DataModificationMessage
                    {
                        Stage = DataModificationMessageStage.BulkComplete,
                        Channel = DataModificationChannel.Bulk,
                        RequestId = transfer.RequestId,
                        TransferId = transfer.TransferId,
                        ModId = transfer.ModId,
                        Operation = transfer.Operation,
                        ChunkCount = transfer.Batches.Count,
                        TotalBytes = transfer.TotalBytes
                    };
                    if (transfer.IsLocalHost)
                        ReceiveOnHost(completeMessage, 0);
                    else
                        NetworkMessageSender.SendDataModificationMessage(0, completeMessage);
                    transfer.CompleteSent = true;
                }
                if (chunks >= chunkBudget || bytes >= byteBudget)
                    break;
            }
        }

        private void ProcessHostFastRequests()
        {
            int budget = Math.Max(1, Math.Min(MaximumFastRequestsPerFrame,
                ScMultiplayerSettings.DataModificationFastMaxConcurrent));
            int processed = 0;
            while (processed < budget && m_hostFastRequests.Count > 0)
            {
                HostFastRequest request = m_hostFastRequests.Dequeue();
                m_pendingFastKeys.Remove(ComposeKey(request.SourceClientId,
                    request.Message.RequestId));
                DataModificationResult result = ApplyFast(request);
                CacheFastResult(request.SourceClientId, request.Message.RequestId, result);
                SendResult(request.SourceClientId, result);
                processed++;
            }
        }

        private void ProcessHostBulkTransfers(double now)
        {
            if (m_hostBulkOrder.Count == 0)
                return;
            int chunkBudget = Math.Max(1, ScMultiplayerSettings
                .DataModificationBulkApplyChunksPerFrame);
            int byteBudget = Math.Max(MaximumBulkChunkBytes, ScMultiplayerSettings
                .DataModificationBulkApplyBytesPerFrame);
            int applied = 0;
            int appliedBytes = 0;
            bool madeProgress = true;
            while (madeProgress && applied < chunkBudget && appliedBytes < byteBudget &&
                m_hostBulkOrder.Count > 0)
            {
                madeProgress = false;
                if (m_hostBulkCursor >= m_hostBulkOrder.Count)
                    m_hostBulkCursor = 0;
                int examined = 0;
                while (examined < m_hostBulkOrder.Count && applied < chunkBudget &&
                    appliedBytes < byteBudget)
                {
                    if (m_hostBulkOrder.Count == 0)
                        break;
                    if (m_hostBulkCursor >= m_hostBulkOrder.Count)
                        m_hostBulkCursor = 0;
                    HostBulkTransfer transfer = m_hostBulkOrder[m_hostBulkCursor++];
                    examined++;
                    if (now - transfer.LastActivityTime > BulkTimeoutSeconds)
                    {
                        RemoveHostBulk(transfer, DataModificationResultCode.Cancelled,
                            "Bulk data modification timed out.");
                        madeProgress = true;
                        continue;
                    }
                    if (!transfer.Chunks.TryGetValue(transfer.NextApplyIndex,
                        out byte[] payload))
                    {
                        continue;
                    }
                    // Earlier chunks may be streamed into the host Mod immediately. Hold only the
                    // final logical chunk until Complete is received so IsFinalChunk is reliable
                    // even though chunks and control messages use different delivery modes.
                    if (transfer.NextApplyIndex == transfer.ChunkCount - 1 &&
                        !transfer.CompleteReceived)
                    {
                        continue;
                    }
                    if (applied > 0 && appliedBytes + payload.Length > byteBudget)
                        continue;
                    transfer.Chunks.Remove(transfer.NextApplyIndex);
                    DataModificationApplyContext context = CreateApplyContext(transfer,
                        transfer.NextApplyIndex, payload,
                        transfer.CompleteReceived && transfer.NextApplyIndex == transfer.ChunkCount - 1);
                    if (!TryApply(context, out string error))
                    {
                        RemoveHostBulk(transfer, DataModificationResultCode.Failed,
                            error ?? "Host Mod rejected the bulk chunk.");
                        madeProgress = true;
                        continue;
                    }
                    transfer.NextApplyIndex++;
                    transfer.AppliedChunks++;
                    transfer.BufferedBytes -= payload.Length;
                    applied++;
                    appliedBytes += payload.Length;
                    madeProgress = true;
                    if (transfer.CompleteReceived &&
                        transfer.NextApplyIndex >= transfer.ChunkCount)
                    {
                        if (transfer.ReceivedChunks == transfer.ChunkCount &&
                            transfer.ReceivedBytes == transfer.TotalBytes)
                        {
                            RemoveHostBulk(transfer, DataModificationResultCode.Applied,
                                "Authoritative bulk modification applied.");
                        }
                        else
                        {
                            RemoveHostBulk(transfer, DataModificationResultCode.Invalid,
                                "Bulk DM ended with mismatched chunks or bytes.");
                        }
                    }
                }
            }
        }

        private void ReceiveOnHost(DataModificationMessage message, int sourceClientId)
        {
            switch (message.Stage)
            {
                case DataModificationMessageStage.FastRequest:
                    ReceiveHostFast(message, sourceClientId);
                    break;
                case DataModificationMessageStage.BulkBegin:
                    ReceiveHostBulkBegin(message, sourceClientId);
                    break;
                case DataModificationMessageStage.BulkChunk:
                    ReceiveHostBulkChunk(message, sourceClientId);
                    break;
                case DataModificationMessageStage.BulkComplete:
                    ReceiveHostBulkComplete(message, sourceClientId);
                    break;
                case DataModificationMessageStage.Cancel:
                    RemoveHostBulkByKey(ComposeKey(sourceClientId, message.TransferId),
                        DataModificationResultCode.Cancelled, "Cancelled by client.");
                    break;
            }
        }

        private void ReceiveHostFast(DataModificationMessage message, int sourceClientId)
        {
            long key = ComposeKey(sourceClientId, message.RequestId);
            if (m_fastResultCache.TryGetValue(key, out DataModificationResult previous))
            {
                SendResult(sourceClientId, previous);
                return;
            }
            if (m_hostFastRequests.Count >= Math.Max(1,
                ScMultiplayerSettings.DataModificationFastMaxConcurrent))
            {
                SendResult(sourceClientId, CreateResult(message, sourceClientId,
                    DataModificationResultCode.Busy, "Fast DM channel is busy."));
                return;
            }
            if (!m_pendingFastKeys.Add(key))
                return;
            DataModificationPolicy policy = ScMultiplayerSettings.DataModificationMode;
            if (policy == DataModificationPolicy.Reject)
            {
                DataModificationResult rejected = CreateResult(message, sourceClientId,
                    DataModificationResultCode.Rejected, "The host rejected DM requests.");
                m_pendingFastKeys.Remove(key);
                CacheFastResult(sourceClientId, message.RequestId, rejected);
                SendResult(sourceClientId, rejected);
                return;
            }
            if (policy == DataModificationPolicy.Default &&
                !m_owner.IsTrustedDataModificationClient(sourceClientId))
            {
                if (!TryQueueApproval(message, sourceClientId, 1,
                    message.Payload?.Length ?? 0))
                {
                    m_pendingFastKeys.Remove(key);
                    SendResult(sourceClientId, CreateResult(message, sourceClientId,
                        DataModificationResultCode.Busy,
                        "The host DM approval queue is full."));
                }
                return;
            }
            EnqueueApprovedFast(message, sourceClientId);
        }

        private void ReceiveHostBulkBegin(DataModificationMessage message, int sourceClientId)
        {
            long key = ComposeKey(sourceClientId, message.TransferId);
            if (m_hostBulkTransfers.ContainsKey(key))
            {
                SendResult(sourceClientId, CreateResult(message, sourceClientId,
                    DataModificationResultCode.Accepted, "Bulk transfer already accepted."));
                return;
            }
            long approvalKey = ComposeApprovalKey(message, sourceClientId);
            if (m_pendingApprovals.ContainsKey(approvalKey))
                return;
            if (message.Channel != DataModificationChannel.Bulk || message.ChunkCount <= 0 ||
                message.ChunkCount > MaximumBulkChunks || message.TotalBytes <= 0 ||
                message.TotalBytes > MaximumBulkBytes || m_hostBulkOrder.Count >=
                Math.Max(1, ScMultiplayerSettings.DataModificationBulkMaxConcurrent))
            {
                SendResult(sourceClientId, CreateResult(message, sourceClientId,
                    DataModificationResultCode.Busy, "Bulk DM channels are full or invalid."));
                return;
            }
            DataModificationPolicy policy = ScMultiplayerSettings.DataModificationMode;
            if (policy == DataModificationPolicy.Reject)
            {
                SendResult(sourceClientId, CreateResult(message, sourceClientId,
                    DataModificationResultCode.Rejected, "The host rejected DM requests."));
                return;
            }
            if (policy == DataModificationPolicy.Default &&
                !m_owner.IsTrustedDataModificationClient(sourceClientId))
            {
                if (!TryQueueApproval(message, sourceClientId, message.ChunkCount,
                    message.TotalBytes))
                {
                    SendResult(sourceClientId, CreateResult(message, sourceClientId,
                        DataModificationResultCode.Busy,
                        "The host DM approval queue is full."));
                }
                return;
            }
            AcceptHostBulk(message, sourceClientId);
        }

        private void AcceptHostBulk(DataModificationMessage message, int sourceClientId)
        {
            if (m_hostBulkOrder.Count >= Math.Max(1,
                ScMultiplayerSettings.DataModificationBulkMaxConcurrent))
            {
                SendResult(sourceClientId, CreateResult(message, sourceClientId,
                    DataModificationResultCode.Busy, "Bulk DM channels are full."));
                return;
            }
            long key = ComposeKey(sourceClientId, message.TransferId);
            if (m_hostBulkTransfers.ContainsKey(key))
                return;
            var transfer = new HostBulkTransfer
            {
                SourceClientId = sourceClientId,
                RequestId = message.RequestId,
                TransferId = message.TransferId,
                ModId = message.ModId,
                Operation = message.Operation,
                ChunkCount = message.ChunkCount,
                TotalBytes = message.TotalBytes,
                LastActivityTime = Time.RealTime
            };
            m_hostBulkTransfers.Add(key, transfer);
            m_hostBulkOrder.Add(transfer);
            SendResult(sourceClientId, CreateResult(message, sourceClientId,
                DataModificationResultCode.Accepted, "Bulk DM channel accepted."));
        }

        private void ReceiveHostBulkChunk(DataModificationMessage message, int sourceClientId)
        {
            long key = ComposeKey(sourceClientId, message.TransferId);
            if (!m_hostBulkTransfers.TryGetValue(key, out HostBulkTransfer transfer))
                return;
            if (message.Channel != DataModificationChannel.Bulk ||
                message.RequestId != transfer.RequestId ||
                message.ChunkCount != transfer.ChunkCount ||
                message.TotalBytes != transfer.TotalBytes ||
                !string.Equals(message.ModId, transfer.ModId, StringComparison.Ordinal) ||
                !string.Equals(message.Operation, transfer.Operation, StringComparison.Ordinal) ||
                message.ChunkIndex < 0 || message.ChunkIndex >= transfer.ChunkCount)
            {
                RemoveHostBulk(transfer, DataModificationResultCode.Invalid,
                    "Bulk DM chunk identity does not match the accepted transfer.");
                return;
            }
            transfer.LastActivityTime = Time.RealTime;
            if (transfer.Chunks.ContainsKey(message.ChunkIndex))
                return;
            if (transfer.ReceivedBytes + message.Payload.Length > transfer.TotalBytes ||
                transfer.BufferedBytes + message.Payload.Length > MaximumBufferedBulkBytes)
            {
                RemoveHostBulk(transfer, DataModificationResultCode.Busy,
                    "Bulk DM buffer is full.");
                return;
            }
            transfer.Chunks.Add(message.ChunkIndex, message.Payload ?? Array.Empty<byte>());
            transfer.ReceivedChunks++;
            transfer.ReceivedBytes += message.Payload.Length;
            transfer.BufferedBytes += message.Payload.Length;
        }

        private void ReceiveHostBulkComplete(DataModificationMessage message, int sourceClientId)
        {
            if (m_hostBulkTransfers.TryGetValue(ComposeKey(sourceClientId, message.TransferId),
                out HostBulkTransfer transfer) && message.RequestId == transfer.RequestId)
            {
                if (message.Channel != DataModificationChannel.Bulk ||
                    message.ChunkCount != transfer.ChunkCount ||
                    message.TotalBytes != transfer.TotalBytes ||
                    !string.Equals(message.ModId, transfer.ModId, StringComparison.Ordinal) ||
                    !string.Equals(message.Operation, transfer.Operation, StringComparison.Ordinal))
                {
                    RemoveHostBulk(transfer, DataModificationResultCode.Invalid,
                        "Bulk DM completion identity does not match the accepted transfer.");
                    return;
                }
                // Bulk chunks use reliable unordered delivery, while Complete uses the ordered
                // control path. Complete can arrive before an earlier chunk, so the transfer stays
                // open until every indexed chunk arrives or the normal timeout expires.
                transfer.CompleteReceived = true;
                transfer.LastActivityTime = Time.RealTime;
            }
        }

        private void ReceiveOnClient(DataModificationMessage message)
        {
            if (message.Stage != DataModificationMessageStage.Result)
                return;
            if (message.Channel == DataModificationChannel.Bulk &&
                m_clientBulkTransfers.TryGetValue(message.TransferId, out ClientBulkTransfer transfer))
            {
                if (message.ResultCode == DataModificationResultCode.Accepted)
                {
                    transfer.Accepted = true;
                    transfer.ServerBulkChunksPerFrame = message.ServerBulkChunksPerFrame;
                    transfer.ServerBulkBytesPerFrame = message.ServerBulkBytesPerFrame;
                    return;
                }
                if (message.ResultCode == DataModificationResultCode.Applied ||
                    message.ResultCode == DataModificationResultCode.Failed ||
                    message.ResultCode == DataModificationResultCode.Cancelled ||
                    message.ResultCode == DataModificationResultCode.Rejected ||
                    message.ResultCode == DataModificationResultCode.Busy ||
                    message.ResultCode == DataModificationResultCode.Invalid ||
                    message.ResultCode == DataModificationResultCode.NotSupported)
                {
                    m_clientBulkTransfers.Remove(message.TransferId);
                }
            }
            m_owner.PublishDataModificationResult(new DataModificationResult
            {
                Code = message.ResultCode,
                SourceClientId = 0,
                Channel = message.Channel,
                ModId = message.ModId,
                Operation = message.Operation,
                RequestId = message.RequestId,
                TransferId = message.TransferId,
                ChunkIndex = message.ChunkIndex,
                TotalChunks = message.ChunkCount,
                Details = message.Details ?? string.Empty
            });
        }

        public DataModificationApprovalRequest GetNextPendingApproval()
        {
            while (m_pendingApprovalOrder.Count > 0)
            {
                long key = m_pendingApprovalOrder.Peek();
                if (m_pendingApprovals.TryGetValue(key, out PendingApproval pending))
                    return pending.Request;
                m_pendingApprovalOrder.Dequeue();
            }
            return null;
        }

        public IReadOnlyList<DataModificationApprovalRequest> GetPendingApprovals()
        {
            var result = new List<DataModificationApprovalRequest>();
            foreach (PendingApproval pending in m_pendingApprovals.Values)
                result.Add(pending.Request);
            result.Sort((left, right) => left.ReceivedTime.CompareTo(right.ReceivedTime));
            return result;
        }

        public bool ResolveApproval(int sourceClientId, int requestId, int transferId,
            bool allow)
        {
            DataModificationChannel channel = transferId > 0
                ? DataModificationChannel.Bulk
                : DataModificationChannel.Fast;
            long key = ComposeApprovalKey(sourceClientId, channel, requestId, transferId);
            if (!m_pendingApprovals.TryGetValue(key, out PendingApproval pending) ||
                pending.Request.RequestId != requestId ||
                pending.Request.TransferId != transferId)
                return false;
            ResolvePendingApproval(pending, allow,
                allow ? "Host approved the DM request." : "Host rejected the DM request.");
            return true;
        }

        public bool ResolveApproval(DataModificationApprovalRequest request, bool allow)
        {
            if (request == null)
                return false;
            long key = ComposeApprovalKey(request.SourceClientId, request.Channel,
                request.RequestId, request.TransferId);
            if (!m_pendingApprovals.TryGetValue(key, out PendingApproval pending) ||
                !ReferenceEquals(pending.Request, request))
                return false;
            ResolvePendingApproval(pending, allow,
                allow ? "Host approved the DM request." : "Host rejected the DM request.");
            return true;
        }

        public bool IsApprovalPending(DataModificationApprovalRequest request)
        {
            if (request == null)
                return false;
            long key = ComposeApprovalKey(request.SourceClientId, request.Channel,
                request.RequestId, request.TransferId);
            return m_pendingApprovals.TryGetValue(key, out PendingApproval pending) &&
                ReferenceEquals(pending.Request, request);
        }

        private void ProcessPendingApprovals(double now)
        {
            if (m_pendingApprovals.Count == 0)
                return;
            DataModificationPolicy policy = ScMultiplayerSettings.DataModificationMode;
            var pending = new List<PendingApproval>(m_pendingApprovals.Values);
            for (int i = 0; i < pending.Count; i++)
            {
                PendingApproval item = pending[i];
                if (!m_pendingApprovals.ContainsKey(item.Key))
                    continue;
                if (policy == DataModificationPolicy.Allow)
                {
                    ResolvePendingApproval(item, true,
                        "The host changed DM policy to allow.");
                }
                else if (policy == DataModificationPolicy.Reject ||
                    now - item.Request.ReceivedTime >= ApprovalTimeoutSeconds)
                {
                    ResolvePendingApproval(item, false,
                        policy == DataModificationPolicy.Reject
                            ? "The host changed DM policy to reject."
                            : "Host DM approval timed out.");
                }
            }
        }

        private bool TryQueueApproval(DataModificationMessage message, int sourceClientId,
            int chunkCount, int totalBytes)
        {
            long key = ComposeApprovalKey(message, sourceClientId);
            if (m_pendingApprovals.ContainsKey(key))
                return true;
            if (m_pendingApprovals.Count >= MaximumPendingApprovals)
                return false;
            var pending = new PendingApproval
            {
                Key = key,
                SourceClientId = sourceClientId,
                Message = message,
                Request = new DataModificationApprovalRequest
                {
                    SourceClientId = sourceClientId,
                    SourcePlayerIndex = m_owner.ResolveDataModificationPlayerIndex(sourceClientId),
                    Channel = message.Channel,
                    ModId = message.ModId,
                    Operation = message.Operation,
                    RequestId = message.RequestId,
                    TransferId = message.TransferId,
                    ChunkCount = chunkCount,
                    TotalBytes = totalBytes,
                    ReceivedTime = Time.RealTime,
                    Summary = ScMultiplayer.DescribeDataModificationRequestSummary(
                        message.Operation, message.Payload),
                    Payload = message.Channel == DataModificationChannel.Fast &&
                        (message.Payload?.Length ?? 0) <= 16 * 1024
                            ? message.Payload ?? Array.Empty<byte>()
                            : Array.Empty<byte>()
                }
            };
            m_pendingApprovals.Add(key, pending);
            m_pendingApprovalOrder.Enqueue(key);
            m_owner.PublishDataModificationApprovalRequest(pending.Request);
            return true;
        }

        private void ResolvePendingApproval(PendingApproval pending, bool allow,
            string details)
        {
            if (pending == null || !m_pendingApprovals.Remove(pending.Key))
                return;
            DataModificationMessage message = pending.Message;
            if (message.Channel == DataModificationChannel.Fast)
            {
                if (allow)
                {
                    EnqueueApprovedFast(message, pending.SourceClientId);
                    return;
                }
                m_pendingFastKeys.Remove(ComposeKey(pending.SourceClientId,
                    message.RequestId));
                DataModificationResult rejected = CreateResult(message,
                    pending.SourceClientId, DataModificationResultCode.Rejected, details);
                CacheFastResult(pending.SourceClientId, message.RequestId, rejected);
                SendResult(pending.SourceClientId, rejected);
                return;
            }
            if (allow)
                AcceptHostBulk(message, pending.SourceClientId);
            else
                SendResult(pending.SourceClientId, CreateResult(message,
                    pending.SourceClientId, DataModificationResultCode.Rejected, details));
        }

        private void EnqueueApprovedFast(DataModificationMessage message, int sourceClientId)
        {
            if (m_hostFastRequests.Count >= Math.Max(1,
                ScMultiplayerSettings.DataModificationFastMaxConcurrent))
            {
                m_pendingFastKeys.Remove(ComposeKey(sourceClientId, message.RequestId));
                DataModificationResult busy = CreateResult(message, sourceClientId,
                    DataModificationResultCode.Busy, "Fast DM channel is busy.");
                CacheFastResult(sourceClientId, message.RequestId, busy);
                SendResult(sourceClientId, busy);
                return;
            }
            m_hostFastRequests.Enqueue(new HostFastRequest
            {
                SourceClientId = sourceClientId,
                Message = message
            });
        }

        private DataModificationResult ApplyFast(HostFastRequest request)
        {
            DataModificationApplyContext context = new DataModificationApplyContext
            {
                SourceClientId = request.SourceClientId,
                SourcePlayerIndex = m_owner.ResolveDataModificationPlayerIndex(
                    request.SourceClientId),
                Channel = DataModificationChannel.Fast,
                ModId = request.Message.ModId,
                Operation = request.Message.Operation,
                RequestId = request.Message.RequestId,
                Payload = request.Message.Payload ?? Array.Empty<byte>(),
                ChunkIndex = 0,
                ChunkCount = 1,
                IsFinalChunk = true
            };
            if (!TryApply(context, out string error))
                return CreateResult(request.Message, request.SourceClientId,
                    error == null ? DataModificationResultCode.NotSupported :
                    DataModificationResultCode.Failed, error ?? "No host-side DM handler accepted the request.");
            return CreateResult(request.Message, request.SourceClientId,
                DataModificationResultCode.Applied, "Authoritative fast modification applied.");
        }

        private bool TryApply(DataModificationApplyContext context, out string error)
        {
            error = null;
            if (DataModificationOperationNames.IsBuiltIn(context.Operation))
            {
                DataModificationApplyResult builtInResult =
                    m_owner.ApplyBuiltInDataModification(context);
                if (builtInResult?.Applied == true)
                    return true;
                error = NormalizeDetails(builtInResult?.Details);
                return false;
            }
            // 联机 mod 自己就能落地的通用数据修改（世界设置…）：主机端不需要安装任何第三方 mod。
            DataModificationApplyResult internalResult =
                m_owner.ApplyHostInternalDataModification(context);
            if (internalResult != null)
            {
                if (internalResult.Applied)
                    return true;
                error = NormalizeDetails(internalResult.Details);
                return false;
            }
            object[][] responses = m_owner.TriggerDataModificationEvent(
                DataModificationEvents.Apply, context);
            for (int i = 0; i < responses.Length; i++)
            {
                object[] response = responses[i];
                if (response == null || response.Length == 0)
                    continue;
                if (response[0] is DataModificationApplyResult result)
                {
                    if (result.Applied)
                        return true;
                    error = NormalizeDetails(result.Details);
                }
                else if (response[0] is bool accepted && accepted)
                {
                    return true;
                }
            }
            return false;
        }

        private void SendResult(int targetClientId, DataModificationResult result)
        {
            var message = new DataModificationMessage
            {
                Stage = DataModificationMessageStage.Result,
                Channel = result.Channel,
                RequestId = result.RequestId,
                TransferId = result.TransferId,
                ModId = result.ModId,
                Operation = result.Operation,
                ChunkIndex = result.ChunkIndex,
                ChunkCount = result.TotalChunks,
                ResultCode = result.Code,
                Details = NormalizeDetails(result.Details),
                ServerBulkChunksPerFrame = ScMultiplayerSettings
                    .DataModificationBulkApplyChunksPerFrame,
                ServerBulkBytesPerFrame = ScMultiplayerSettings
                    .DataModificationBulkApplyBytesPerFrame
            };
            if (targetClientId == 0 && ScMultiplayer.IsHost)
            {
                ReceiveOnClient(message);
                return;
            }
            NetworkMessageSender.SendDataModificationMessage(targetClientId, message);
        }

        private void RemoveHostBulk(HostBulkTransfer transfer,
            DataModificationResultCode code, string details)
        {
            if (transfer == null)
                return;
            long key = ComposeKey(transfer.SourceClientId, transfer.TransferId);
            m_hostBulkTransfers.Remove(key);
            m_hostBulkOrder.Remove(transfer);
            if (m_hostBulkCursor > m_hostBulkOrder.Count)
                m_hostBulkCursor = 0;
            SendResult(transfer.SourceClientId, new DataModificationResult
            {
                Code = code,
                SourceClientId = transfer.SourceClientId,
                Channel = DataModificationChannel.Bulk,
                ModId = transfer.ModId,
                Operation = transfer.Operation,
                RequestId = transfer.RequestId,
                TransferId = transfer.TransferId,
                AppliedChunks = transfer.AppliedChunks,
                TotalChunks = transfer.ChunkCount,
                Details = details
            });
        }

        private void RemoveHostBulkByKey(long key, DataModificationResultCode code,
            string details)
        {
            if (m_hostBulkTransfers.TryGetValue(key, out HostBulkTransfer transfer))
                RemoveHostBulk(transfer, code, details);
        }

        private void CacheFastResult(int sourceClientId, int requestId,
            DataModificationResult result)
        {
            long key = ComposeKey(sourceClientId, requestId);
            m_fastResultCache[key] = result;
            m_fastResultOrder.Enqueue(key);
            while (m_fastResultOrder.Count > 512)
                m_fastResultCache.Remove(m_fastResultOrder.Dequeue());
        }

        private DataModificationMessage CreateFastMessage(
            DataModificationSubmitRequest request)
        {
            return new DataModificationMessage
            {
                Stage = DataModificationMessageStage.FastRequest,
                Channel = DataModificationChannel.Fast,
                RequestId = request.RequestId,
                ModId = request.ModId,
                Operation = request.Operation,
                Payload = request.Payload ?? Array.Empty<byte>()
            };
        }

        private DataModificationMessage CreateBulkBeginMessage(
            DataModificationSubmitRequest request)
        {
            return new DataModificationMessage
            {
                Stage = DataModificationMessageStage.BulkBegin,
                Channel = DataModificationChannel.Bulk,
                RequestId = request.RequestId,
                TransferId = request.TransferId,
                ModId = request.ModId,
                Operation = request.Operation,
                ChunkCount = request.Batches.Count,
                TotalBytes = GetTotalBytes(request.Batches)
            };
        }

        private ClientBulkTransfer CreateClientBulkTransfer(
            DataModificationSubmitRequest request)
        {
            var batches = new List<byte[]>(request.Batches.Count);
            foreach (byte[] batch in request.Batches)
                batches.Add(batch != null ? (byte[])batch.Clone() : Array.Empty<byte>());
            DataModificationMessage begin = CreateBulkBeginMessage(request);
            var transfer = new ClientBulkTransfer
            {
                RequestId = begin.RequestId,
                TransferId = request.TransferId,
                ModId = request.ModId,
                Operation = request.Operation,
                Batches = batches,
                TotalBytes = begin.TotalBytes
            };
            transfer.BeginMessage = begin;
            return transfer;
        }

        private static int GetTotalBytes(IReadOnlyList<byte[]> batches)
        {
            long total = 0;
            if (batches != null)
                for (int i = 0; i < batches.Count; i++)
                    total += batches[i]?.Length ?? 0;
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        private static bool TryNormalizeRequest(DataModificationSubmitRequest request,
            out DataModificationSubmitRequest normalized, out string error)
        {
            normalized = null;
            error = null;
            if (request == null)
            {
                error = "The DM request is null.";
                return false;
            }
            string modId = NormalizeIdentity(request.ModId, 64);
            string operation = NormalizeIdentity(request.Operation, 64);
            if (modId == null || operation == null)
            {
                error = "ModId and operation must be 1-64 characters.";
                return false;
            }
            if (request.Channel == DataModificationChannel.Fast)
            {
                byte[] payload = request.Payload ?? Array.Empty<byte>();
                if (payload.Length > MaximumFastPayloadBytes)
                {
                    error = "Fast DM payload exceeds 8192 bytes.";
                    return false;
                }
                normalized = new DataModificationSubmitRequest
                {
                    Channel = DataModificationChannel.Fast,
                    ModId = modId,
                    Operation = operation,
                    Payload = (byte[])payload.Clone()
                };
                return true;
            }
            if (request.Channel != DataModificationChannel.Bulk || request.Batches == null ||
                request.Batches.Count == 0 || request.Batches.Count > MaximumBulkChunks)
            {
                error = "Bulk DM requires 1-65535 logical chunks.";
                return false;
            }
            long total = 0;
            var batches = new List<byte[]>(request.Batches.Count);
            for (int i = 0; i < request.Batches.Count; i++)
            {
                byte[] batch = request.Batches[i] ?? Array.Empty<byte>();
                if (batch.Length == 0 || batch.Length > MaximumBulkChunkBytes)
                {
                    error = "Each bulk DM chunk must contain 1-768 bytes.";
                    return false;
                }
                total += batch.Length;
                if (total > MaximumBulkBytes)
                {
                    error = "Bulk DM payload exceeds 16 MiB.";
                    return false;
                }
                batches.Add((byte[])batch.Clone());
            }
            normalized = new DataModificationSubmitRequest
            {
                Channel = DataModificationChannel.Bulk,
                ModId = modId,
                Operation = operation,
                Batches = batches
            };
            return true;
        }

        private static string NormalizeIdentity(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            string normalized = value.Trim();
            if (normalized.Length == 0 || normalized.Length > maximumLength ||
                normalized.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                return null;
            return normalized;
        }

        private static string NormalizeDetails(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= 256 ? normalized : normalized.Substring(0, 256);
        }

        private DataModificationResult CreateResult(DataModificationMessage message,
            int sourceClientId, DataModificationResultCode code, string details)
        {
            return new DataModificationResult
            {
                Code = code,
                SourceClientId = sourceClientId,
                Channel = message.Channel,
                ModId = message.ModId,
                Operation = message.Operation,
                RequestId = message.RequestId,
                TransferId = message.TransferId,
                ChunkIndex = message.ChunkIndex,
                TotalChunks = message.ChunkCount,
                Details = NormalizeDetails(details)
            };
        }

        private static DataModificationSubmitResult Rejected(string details) =>
            new DataModificationSubmitResult
            {
                Code = DataModificationResultCode.Rejected,
                Details = details ?? string.Empty
            };

        private static long ComposeKey(int clientId, int value) =>
            ((long)clientId << 32) ^ (uint)value;

        private static long ComposeApprovalKey(DataModificationMessage message,
            int sourceClientId) => ComposeApprovalKey(sourceClientId, message.Channel,
                message.RequestId, message.TransferId);

        private static long ComposeApprovalKey(int sourceClientId,
            DataModificationChannel channel, int requestId, int transferId) =>
            ComposeKey(sourceClientId, channel == DataModificationChannel.Fast
                ? requestId : -transferId);

        private int NextRequestId() => NextPositive(ref m_nextRequestId);

        private int NextTransferId() => NextPositive(ref m_nextTransferId);

        private static int NextPositive(ref int value)
        {
            int next = System.Threading.Interlocked.Increment(ref value);
            if (next <= 0)
            {
                System.Threading.Interlocked.Exchange(ref value, 1);
                return 1;
            }
            return next;
        }

        private sealed class HostFastRequest
        {
            public int SourceClientId;
            public DataModificationMessage Message;
        }

        private sealed class HostBulkTransfer
        {
            public int SourceClientId;
            public int RequestId;
            public int TransferId;
            public string ModId;
            public string Operation;
            public int ChunkCount;
            public int TotalBytes;
            public int ReceivedChunks;
            public int ReceivedBytes;
            public int BufferedBytes;
            public int NextApplyIndex;
            public int AppliedChunks;
            public bool CompleteReceived;
            public double LastActivityTime;
            public readonly SortedDictionary<int, byte[]> Chunks =
                new SortedDictionary<int, byte[]>();
        }

        private sealed class ClientBulkTransfer
        {
            public int RequestId;
            public int TransferId;
            public string ModId;
            public string Operation;
            public int TotalBytes;
            public int NextChunkIndex;
            public int ServerBulkChunksPerFrame;
            public int ServerBulkBytesPerFrame;
            public bool Accepted;
            public bool CompleteSent;
            public bool IsLocalHost;
            public List<byte[]> Batches;
            public DataModificationMessage BeginMessage;
        }

        private sealed class PendingApproval
        {
            public long Key;
            public int SourceClientId;
            public DataModificationMessage Message;
            public DataModificationApprovalRequest Request;
        }

        private DataModificationApplyContext CreateApplyContext(HostBulkTransfer transfer,
            int chunkIndex, byte[] payload, bool isFinal)
        {
            return new DataModificationApplyContext
            {
                SourceClientId = transfer.SourceClientId,
                SourcePlayerIndex = m_owner.ResolveDataModificationPlayerIndex(
                    transfer.SourceClientId),
                Channel = DataModificationChannel.Bulk,
                ModId = transfer.ModId,
                Operation = transfer.Operation,
                RequestId = transfer.RequestId,
                TransferId = transfer.TransferId,
                ChunkIndex = chunkIndex,
                ChunkCount = transfer.ChunkCount,
                IsFinalChunk = isFinal,
                Payload = payload ?? Array.Empty<byte>()
            };
        }
    }
}

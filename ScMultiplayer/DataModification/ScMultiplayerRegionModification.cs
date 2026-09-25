using Engine;
using Game;
using System;
using System.Globalization;
using System.Text.Json;

namespace ScMultiplayer
{
    /// <summary>
    /// 设计稿 §4 的 `ScMP.Region.*` 指令族。
    ///
    /// 落地口径：
    /// - 指令**由联机 Mod 自己实现**（主机端不需要装 GmMod）；GmMod 只是调用方；
    /// - 增删改一律**只有主机能落地**：客户端提交 → 既有 DM 审核队列（主机弹窗/控制台 Allow）
    ///   → 主机 `ApplyHostRegionModification` 校验并写权威状态 → 广播增量给所有客户端；
    /// - 主机本端面板走**直接落地**（不经 DM），避免"主机自己审批自己"。
    /// </summary>
    internal static class RegionDataOperation
    {
        public const string Claim = "ScMP.Region.Claim";
        public const string Grant = "ScMP.Region.Grant";
        public const string Revoke = "ScMP.Region.Revoke";
        public const string Drop = "ScMP.Region.Drop";
        public const string List = "ScMP.Region.List";

        public static bool IsRegionOperation(string operation) =>
            string.Equals(operation, Claim, StringComparison.Ordinal) ||
            string.Equals(operation, Grant, StringComparison.Ordinal) ||
            string.Equals(operation, Revoke, StringComparison.Ordinal) ||
            string.Equals(operation, Drop, StringComparison.Ordinal) ||
            string.Equals(operation, List, StringComparison.Ordinal);
    }

    /// <summary>
    /// `ScMP.Region.*` 的载荷。X/Z 是选区两点（Y 只是占位，落地时一律整高 0–255）。
    /// </summary>
    public sealed class RegionDataModificationRequest
    {
        public int RegionId { get; set; } = -1;

        public int MinX { get; set; }

        public int MinY { get; set; }

        public int MinZ { get; set; }

        public int MaxX { get; set; }

        public int MaxY { get; set; } = 255;

        public int MaxZ { get; set; }

        public string Name { get; set; } = string.Empty;

        /// <summary>`Grant`/`Revoke` 的目标：优先 `TargetClientId`（在线），否则用 `TargetUserId`。</summary>
        public int TargetClientId { get; set; } = -1;

        public string TargetUserId { get; set; } = string.Empty;
    }

    public static class RegionDataModificationCodec
    {
        public static byte[] Encode(RegionDataModificationRequest request) =>
            JsonSerializer.SerializeToUtf8Bytes(request ?? new RegionDataModificationRequest());

        public static bool TryDecode(byte[] payload, out RegionDataModificationRequest request,
            out string error)
        {
            request = null;
            error = null;
            if (payload == null || payload.Length == 0 || payload.Length > 4096)
            {
                error = "Region operation payload must contain 1-4096 bytes.";
                return false;
            }
            try
            {
                request = JsonSerializer.Deserialize<RegionDataModificationRequest>(payload);
            }
            catch (Exception)
            {
                error = "Region operation payload is not valid JSON.";
                return false;
            }
            if (request == null)
            {
                error = "Region operation payload is empty.";
                return false;
            }
            return true;
        }

        /// <summary>审批弹窗用的一行摘要。</summary>
        public static string Describe(RegionDataModificationRequest request)
        {
            if (request == null)
                return "(empty)";
            return "X " + Math.Min(request.MinX, request.MaxX) + ".." + Math.Max(request.MinX, request.MaxX) +
                " Z " + Math.Min(request.MinZ, request.MaxZ) + ".." + Math.Max(request.MinZ, request.MaxZ) +
                (string.IsNullOrWhiteSpace(request.Name) ? string.Empty : " name=" + request.Name);
        }
    }

    public partial class ScMultiplayer
    {
        /// <summary>
        /// 主机落地 `ScMP.Region.*`：校验 → 调用既有的权威领地 API → 审计 + 增量广播。
        /// 返回 null 表示"不是领地指令"，交给后面的通用 dm 处理。
        /// </summary>
        internal DataModificationApplyResult ApplyHostRegionModification(
            DataModificationApplyContext context)
        {
            if (context == null || !RegionDataOperation.IsRegionOperation(context.Operation))
                return null;
            if (!IsHost || GameManager.Project == null)
                return DataModificationApplyResult.Reject("An active authoritative host is required.");
            if (context.Channel != DataModificationChannel.Fast || context.ChunkCount != 1 ||
                context.ChunkIndex != 0 || !context.IsFinalChunk)
            {
                return DataModificationApplyResult.Reject(
                    "Region operations require one fast single-chunk request.");
            }
            if (!RegionDataModificationCodec.TryDecode(context.Payload,
                out RegionDataModificationRequest request, out string decodeError))
                return DataModificationApplyResult.Reject(decodeError);

            switch (context.Operation)
            {
                case RegionDataOperation.Claim:
                    return ApplyHostRegionClaim(context, request);
                case RegionDataOperation.Drop:
                    return ApplyHostRegionDrop(context, request);
                case RegionDataOperation.Grant:
                    return ApplyHostRegionGrant(context, request);
                case RegionDataOperation.Revoke:
                    return ApplyHostRegionRevoke(context, request);
                case RegionDataOperation.List:
                    return DataModificationApplyResult.Success(
                        "region list: " + RegionClaimCount + " claim(s), sequence=" +
                        RegionClaimSequence.ToString(CultureInfo.InvariantCulture));
                default:
                    return DataModificationApplyResult.Reject("Unsupported region operation.");
            }
        }

        private DataModificationApplyResult ApplyHostRegionClaim(
            DataModificationApplyContext context, RegionDataModificationRequest request)
        {
            if (!IsRegionClaimCoordinateValid(request.MinX) || !IsRegionClaimCoordinateValid(request.MaxX) ||
                !IsRegionClaimCoordinateValid(request.MinZ) || !IsRegionClaimCoordinateValid(request.MaxZ))
            {
                return DataModificationApplyResult.Reject("The claim coordinates are out of range.");
            }
            if (!TryResolveRegionClaimOwnerIdentity(context.SourceClientId, out string ownerKey,
                    out string ownerName) || string.IsNullOrWhiteSpace(ownerKey))
            {
                return DataModificationApplyResult.Reject(
                    "Cannot resolve the requesting player identity.");
            }
            // Source: 设计稿 §1：Y 一律整高 0–255，X/Z 由主机按闭区间收口。
            var a = new Point3(request.MinX, RegionClaimMinimumY, request.MinZ);
            var b = new Point3(request.MaxX, RegionClaimMaximumY, request.MaxZ);
            if (!TryCreateRegionClaim(a, b, request.Name, ownerKey, ownerName, out int id,
                    out string error))
            {
                return DataModificationApplyResult.Reject(error ?? "The claim was rejected.");
            }
            string summary = "#" + id + " " + RegionDataModificationCodec.Describe(request) +
                " owner=" + ownerName;
            Log.Information("[ScMP] Host created region claim from client " +
                context.SourceClientId + ": " + summary);
            PublishServerAudit("region.claim", context.SourceClientId,
                "id=" + id.ToString(CultureInfo.InvariantCulture) + " " + summary + " mod=" +
                NormalizePlayerAdministrationAuditValue(context.ModId));
            return DataModificationApplyResult.Success("region claim #" + id + " created");
        }

        private DataModificationApplyResult ApplyHostRegionDrop(
            DataModificationApplyContext context, RegionDataModificationRequest request)
        {
            if (!TryRemoveRegionClaim(request.RegionId, out string error))
                return DataModificationApplyResult.Reject(error ?? "The claim could not be removed.");
            string summary = "#" + request.RegionId.ToString(CultureInfo.InvariantCulture);
            Log.Information("[ScMP] Host removed region claim from client " +
                context.SourceClientId + ": " + summary);
            PublishServerAudit("region.drop", context.SourceClientId,
                "id=" + summary + " mod=" + NormalizePlayerAdministrationAuditValue(context.ModId));
            return DataModificationApplyResult.Success("region claim " + summary + " dropped");
        }

        private DataModificationApplyResult ApplyHostRegionGrant(
            DataModificationApplyContext context, RegionDataModificationRequest request)
        {
            if (request.TargetClientId < 0)
            {
                return DataModificationApplyResult.Reject(
                    "A target online player is required to grant a claim.");
            }
            if (!TryGrantRegionClaim(request.RegionId, request.TargetClientId, out string error))
                return DataModificationApplyResult.Reject(error ?? "The grant was rejected.");
            string summary = "#" + request.RegionId.ToString(CultureInfo.InvariantCulture) +
                " -> client " + request.TargetClientId.ToString(CultureInfo.InvariantCulture);
            Log.Information("[ScMP] Host granted region claim from client " +
                context.SourceClientId + ": " + summary);
            PublishServerAudit("region.grant", context.SourceClientId,
                summary + " mod=" + NormalizePlayerAdministrationAuditValue(context.ModId));
            return DataModificationApplyResult.Success("region claim granted: " + summary);
        }

        private DataModificationApplyResult ApplyHostRegionRevoke(
            DataModificationApplyContext context, RegionDataModificationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.TargetUserId))
            {
                return DataModificationApplyResult.Reject(
                    "A target user id is required to revoke a claim owner.");
            }
            if (!TryRevokeRegionClaimOwner(request.RegionId, request.TargetUserId, out string error))
                return DataModificationApplyResult.Reject(error ?? "The revoke was rejected.");
            string summary = "#" + request.RegionId.ToString(CultureInfo.InvariantCulture) +
                " owner=" + request.TargetUserId;
            Log.Information("[ScMP] Host revoked region claim owner from client " +
                context.SourceClientId + ": " + summary);
            PublishServerAudit("region.revoke", context.SourceClientId,
                summary + " mod=" + NormalizePlayerAdministrationAuditValue(context.ModId));
            return DataModificationApplyResult.Success("region claim owner revoked: " + summary);
        }

        private static bool IsRegionClaimCoordinateValid(int value) =>
            value >= -(int)MaximumRequestedCoordinate && value <= (int)MaximumRequestedCoordinate;

        /// <summary>
        /// 客户端提交一份 `ScMP.Region.*` 请求（主机端调用会直接落到 DM 队列，由主机审批）。
        /// </summary>
        internal DataModificationSubmitResult RequestRegionModification(string operation,
            RegionDataModificationRequest request)
        {
            if (!RegionDataOperation.IsRegionOperation(operation))
            {
                return new DataModificationSubmitResult
                {
                    Code = DataModificationResultCode.NotSupported,
                    Details = "The requested region operation is not supported."
                };
            }
            if (client?.IsConnected != true)
            {
                return new DataModificationSubmitResult
                {
                    Code = DataModificationResultCode.Rejected,
                    Details = "Not connected to a multiplayer session."
                };
            }
            return SubmitDataModification(new DataModificationSubmitRequest
            {
                Channel = DataModificationChannel.Fast,
                ModId = "ScMP",
                Operation = operation,
                Payload = RegionDataModificationCodec.Encode(request)
            });
        }
    }
}

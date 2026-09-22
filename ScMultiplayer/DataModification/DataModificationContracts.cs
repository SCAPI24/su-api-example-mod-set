using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ScMultiplayer
{
    public enum DataModificationPolicy : byte
    {
        Reject = 0,
        Default = 1,
        Allow = 2
    }

    public enum DataModificationChannel : byte
    {
        Fast = 0,
        Bulk = 1
    }

    [Flags]
    public enum PlayerCapabilityFlags : byte
    {
        None = 0,
        CreativeFly = 1,
        WorldControl = 2,
        CreativeInventory = 4
    }

    /// <summary>
    /// `ScMP.Player.SetVitals` 要改哪几项生命体征（位掩码）。缺席的位表示"保持主机当前值"，
    /// 这样同一份 `PlayerDataModificationRequest` 可以只改饱食度、也可以一次改多项。
    /// </summary>
    [Flags]
    public enum VitalsField : byte
    {
        None = 0,
        Food = 1,
        Stamina = 2,
        Sleep = 4,
        Temperature = 8,
        Wetness = 16,
        All = Food | Stamina | Sleep | Temperature | Wetness
    }

    /// <summary>`ScMP.Player.SetCondition` 作用于哪些异常状态（位掩码）。</summary>
    [Flags]
    public enum ConditionKind : byte
    {
        None = 0,
        Flu = 1,
        Sickness = 2,
        All = Flu | Sickness
    }

    public enum ConditionAction : byte
    {
        /// <summary>解除（0 是默认值，所以"没写"等价于解除；应用方还会用 <see cref="ConditionKind.None"/> 表示"全部解除"）。</summary>
        Clear = 0,
        Apply = 1
    }

    public static class DataModificationOperationNames
    {
        public const string SetRespawnAnchor = "ScMP.Player.SetRespawnAnchor";
        public const string ReturnToPlayer = "ScMP.Player.ReturnToPlayer";
        public const string GrantInventory = "ScMP.Player.GrantInventory";
        public const string RestoreLevel = "ScMP.Player.RestoreLevel";
        public const string HealPlayer = "ScMP.Player.Heal";
        public const string SafeRespawnRelocate = "ScMP.Player.SafeRespawnRelocate";
        public const string SealPlayer = "ScMP.Player.Seal";
        public const string RequestCapabilities = "ScMP.Player.RequestCapabilities";
        public const string RevokeCapabilities = "ScMP.Player.RevokeCapabilities";
        // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerPlayerAdministration.cs:
        // ScMultiplayer.ApplySetVitalsRequest / ApplySetConditionRequest
        // GM 工具（第三方 mod）需要改指定角色的生命体征与异常状态；落地仍由主机完成。
        public const string SetVitals = "ScMP.Player.SetVitals";
        public const string SetCondition = "ScMP.Player.SetCondition";

        public static bool IsBuiltIn(string operation) =>
            string.Equals(operation, SetRespawnAnchor, StringComparison.Ordinal) ||
            string.Equals(operation, ReturnToPlayer, StringComparison.Ordinal) ||
            string.Equals(operation, GrantInventory, StringComparison.Ordinal) ||
            string.Equals(operation, RestoreLevel, StringComparison.Ordinal) ||
            string.Equals(operation, HealPlayer, StringComparison.Ordinal) ||
            string.Equals(operation, SafeRespawnRelocate, StringComparison.Ordinal) ||
            string.Equals(operation, SealPlayer, StringComparison.Ordinal) ||
            string.Equals(operation, RequestCapabilities, StringComparison.Ordinal) ||
            string.Equals(operation, RevokeCapabilities, StringComparison.Ordinal) ||
            string.Equals(operation, SetVitals, StringComparison.Ordinal) ||
            string.Equals(operation, SetCondition, StringComparison.Ordinal);
    }

    // Source: Mod/ScMultiplayer/DataModification/DataModificationTool.cs:
    // DataModificationTool.SubmitFast
    // Coordinates are request hints only. The host resolves the target and final position.
    public sealed class PlayerDataModificationRequest
    {
        public int TargetClientId { get; set; } = -1;
        public int DestinationClientId { get; set; } = -1;
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
        public int ItemValue { get; set; }
        public int ItemCount { get; set; }
        public float Amount { get; set; }
        public float Level { get; set; }
        public bool SetAbsoluteLevel { get; set; }
        public int Radius { get; set; } = 1;
        public int Height { get; set; } = 3;
        public PlayerCapabilityFlags Capabilities { get; set; }
        // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerPlayerAdministration.cs:
        // ScMultiplayer.ApplySetVitalsRequest
        // `ScMP.Player.SetVitals`：`Vitals` 指定位掩码，只有被选中的项会写，其余保持主机当前值。
        public VitalsField Vitals { get; set; }
        public float VitalsFood { get; set; }
        public float VitalsStamina { get; set; }
        public float VitalsSleep { get; set; }
        public float VitalsTemperature { get; set; }
        public float VitalsWetness { get; set; }
        // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerPlayerAdministration.cs:
        // ScMultiplayer.ApplySetConditionRequest
        // `ScMP.Player.SetCondition`：`Condition` 指定位掩码（None = 全部），`ConditionDuration` 秒
        // （0 = 用引擎默认时长）。
        public ConditionKind Condition { get; set; }
        public ConditionAction ConditionMode { get; set; }
        public float ConditionDuration { get; set; }
    }

    public static class PlayerDataModificationCodec
    {
        public static byte[] Encode(PlayerDataModificationRequest request) =>
            JsonSerializer.SerializeToUtf8Bytes(request ?? new PlayerDataModificationRequest());

        public static bool TryDecode(byte[] payload,
            out PlayerDataModificationRequest request, out string error)
        {
            request = null;
            error = null;
            if (payload == null || payload.Length == 0 || payload.Length > 4096)
            {
                error = "Player operation payload must contain 1-4096 bytes.";
                return false;
            }
            try
            {
                request = JsonSerializer.Deserialize<PlayerDataModificationRequest>(payload);
            }
            catch (Exception)
            {
                error = "Player operation payload is not valid JSON.";
                return false;
            }
            if (request == null)
            {
                error = "Player operation payload is empty.";
                return false;
            }
            return true;
        }
    }

    public enum DataModificationResultCode : byte
    {
        Accepted = 0,
        Applied = 1,
        Rejected = 2,
        Busy = 3,
        Invalid = 4,
        NotSupported = 5,
        Failed = 6,
        Cancelled = 7
    }

    public sealed class DataModificationSubmitRequest
    {
        internal int RequestId { get; set; }

        internal int TransferId { get; set; }

        public DataModificationChannel Channel { get; set; }

        public string ModId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public IReadOnlyList<byte[]> Batches { get; set; } = Array.Empty<byte[]>();
    }

    public sealed class DataModificationSubmitResult
    {
        public DataModificationResultCode Code { get; set; }

        public int RequestId { get; set; }

        public int TransferId { get; set; }

        public string Details { get; set; } = string.Empty;

        public bool IsAccepted => Code == DataModificationResultCode.Accepted;
    }

    public sealed class DataModificationApprovalRequest
    {
        public int SourceClientId { get; set; }

        public int SourcePlayerIndex { get; set; } = -1;

        /// <summary>
        /// 发起方的**记录键**（= `UserManager.ActiveUser.UniqueId`，即账号 userid；
        /// 无身份时为 `name:名字`）。名字可改，这个键不可改，主机侧授权一律按它判断。
        /// </summary>
        public string SourceKey { get; set; } = string.Empty;

        public DataModificationChannel Channel { get; set; }

        public string ModId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public int RequestId { get; set; }

        public int TransferId { get; set; }

        public int ChunkCount { get; set; }

        public int TotalBytes { get; set; }

        public double ReceivedTime { get; set; }

        /// <summary>请求摘要（主机审批弹窗显示“改了什么”）。</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>请求载荷（仅快速通道且体积受限时保留，供摘要解析；不长期持有大载荷）。</summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    public sealed class DataModificationApplyContext
    {
        public int SourceClientId { get; set; }

        public int SourcePlayerIndex { get; set; } = -1;

        public DataModificationChannel Channel { get; set; }

        public string ModId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public int RequestId { get; set; }

        public int TransferId { get; set; }

        public int ChunkIndex { get; set; }

        public int ChunkCount { get; set; }

        public bool IsFinalChunk { get; set; }

        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    public sealed class DataModificationApplyResult
    {
        public bool Applied { get; set; }

        public string Details { get; set; } = string.Empty;

        public static DataModificationApplyResult Success(string details = null) =>
            new DataModificationApplyResult { Applied = true, Details = details ?? string.Empty };

        public static DataModificationApplyResult Reject(string details) =>
            new DataModificationApplyResult { Applied = false, Details = details ?? string.Empty };
    }

    public sealed class DataModificationResult
    {
        public DataModificationResultCode Code { get; set; }

        public int SourceClientId { get; set; }

        public DataModificationChannel Channel { get; set; }

        public string ModId { get; set; } = string.Empty;

        public string Operation { get; set; } = string.Empty;

        public int RequestId { get; set; }

        public int TransferId { get; set; }

        public int ChunkIndex { get; set; } = -1;

        public int AppliedChunks { get; set; }

        public int TotalChunks { get; set; }

        public string Details { get; set; } = string.Empty;
    }

    public static class DataModificationEvents
    {
        public const string Submit = "ScMultiplayer.DataModification.Submit";
        public const string ApprovalControl =
            "ScMultiplayer.DataModification.ApprovalControl";
        public const string ApprovalRequested =
            "ScMultiplayer.DataModification.ApprovalRequested";
        public const string Apply = "ScMultiplayer.DataModification.Apply";
        public const string Result = "ScMultiplayer.DataModification.Result";
    }

    // Source: Mod/ScMultiplayer/Networking/NetworkMessageSender.cs:
    // DataModificationCoordinator.Submit
    // This is the optional-mod facade. It only submits opaque data; the host-side handler owns
    // all game mutations and must use the original game APIs or existing ScMultiplayer adapters.
    public static class DataModificationTool
    {
        public static event Action<DataModificationResult> ResultReceived;

        public static DataModificationSubmitResult SubmitFast(string modId, string operation,
            byte[] payload) => ScMultiplayer.currentInstance?.SubmitDataModification(
                new DataModificationSubmitRequest
                {
                    Channel = DataModificationChannel.Fast,
                    ModId = modId,
                    Operation = operation,
                    Payload = payload
                }) ?? new DataModificationSubmitResult
                {
                    Code = DataModificationResultCode.Rejected,
                    Details = "ScMultiplayer is not active."
                };

        public static DataModificationSubmitResult SubmitBulk(string modId, string operation,
            IReadOnlyList<byte[]> batches) => ScMultiplayer.currentInstance?.SubmitDataModification(
                new DataModificationSubmitRequest
                {
                    Channel = DataModificationChannel.Bulk,
                    ModId = modId,
                    Operation = operation,
                    Batches = batches
                }) ?? new DataModificationSubmitResult
                {
                    Code = DataModificationResultCode.Rejected,
                    Details = "ScMultiplayer is not active."
                };

        // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerPlayerAdministration.cs:
        // ScMultiplayer.ApplyBuiltInDataModification
        // This method only submits an application. The authoritative host resolves the target,
        // validates the payload and performs the actual game mutation.
        public static DataModificationSubmitResult RequestPlayerModification(string modId,
            string operation, PlayerDataModificationRequest request)
        {
            if (!DataModificationOperationNames.IsBuiltIn(operation))
            {
                return new DataModificationSubmitResult
                {
                    Code = DataModificationResultCode.NotSupported,
                    Details = "The requested player operation is not supported."
                };
            }
            return SubmitFast(modId, operation,
                PlayerDataModificationCodec.Encode(request));
        }

        public static DataModificationSubmitResult RequestPlayerCapabilities(string modId,
            PlayerCapabilityFlags capabilities) => RequestPlayerModification(modId,
                DataModificationOperationNames.RequestCapabilities,
                new PlayerDataModificationRequest { Capabilities = capabilities });

        public static DataModificationSubmitResult RevokePlayerCapabilities(string modId,
            PlayerCapabilityFlags capabilities) => RequestPlayerModification(modId,
                DataModificationOperationNames.RevokeCapabilities,
                new PlayerDataModificationRequest { Capabilities = capabilities });

        /// <summary>
        /// 在线玩家列表，供第三方 GM 工具做"目标选择"（跨玩家操作需要目标 clientId）。
        /// 每项：`clientId` / `name` / `playerIndex` / `isSelf` / `isHost`。
        /// 用普通字典返回，第三方 mod 不需要引用 ScMultiplayer 的内部类型。
        /// </summary>
        public static List<Dictionary<string, object>> DescribeOnlinePlayers()
        {
            var result = new List<Dictionary<string, object>>();
            ScMultiplayer.currentInstance?.DescribeOnlinePlayersInto(result);
            return result;
        }

        internal static void RaiseResult(DataModificationResult result)
        {
            try
            {
                ResultReceived?.Invoke(result);
            }
            catch
            {
                // A diagnostic/result observer must never affect the authoritative update loop.
            }
        }
    }
}

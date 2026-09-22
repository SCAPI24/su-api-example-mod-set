using ScMultiplayer.Core;
using ScMultiplayer.Ports;
using Game;
using SuAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private DataModificationCoordinator m_dataModification;
        private EventSubscriptionToken m_dataModificationSubmitToken;
        private EventSubscriptionToken m_dataModificationApprovalControlToken;
        private Dialog m_activeDataModificationApprovalDialog;
        private DataModificationApprovalRequest m_activeDataModificationApproval;

        internal void InitializeDataModification(IModEventBus eventBus)
        {
            m_dataModification ??= new DataModificationCoordinator(this);
            if (eventBus != null && m_dataModificationSubmitToken == null)
            {
                m_dataModificationSubmitToken = eventBus.SubscribeEvent(
                    DataModificationEvents.Submit,
                    HandleDataModificationSubmit,
                    EventPriority.HIGHEST);
            }
            if (eventBus != null && m_dataModificationApprovalControlToken == null)
            {
                m_dataModificationApprovalControlToken = eventBus.SubscribeEvent(
                    DataModificationEvents.ApprovalControl,
                    HandleDataModificationApprovalControl,
                    EventPriority.HIGHEST);
            }
        }

        internal object[] HandleDataModificationSubmit(object[] args)
        {
            DataModificationSubmitRequest request = args != null && args.Length > 0
                ? args[0] as DataModificationSubmitRequest : null;
            return new object[] { SubmitDataModification(request) };
        }

        internal object[] HandleDataModificationApprovalControl(object[] args)
        {
            IDictionary<string, object> request = args != null && args.Length > 0
                ? args[0] as IDictionary<string, object>
                : null;
            string operation = request != null &&
                request.TryGetValue("operation", out object operationValue)
                    ? operationValue?.ToString()
                    : "list";
            if (string.Equals(operation, "resolve", StringComparison.OrdinalIgnoreCase))
            {
                bool resolved = false;
                if (IsHost && request != null &&
                    TryReadApprovalInteger(request, "sourceClientId", out int sourceClientId) &&
                    TryReadApprovalInteger(request, "requestId", out int requestId) &&
                    TryReadApprovalInteger(request, "transferId", out int transferId) &&
                    TryReadApprovalBoolean(request, "allow", out bool allow))
                {
                    resolved = m_dataModification?.ResolveApproval(sourceClientId,
                        requestId, transferId, allow) == true;
                    if (resolved && m_activeDataModificationApproval != null &&
                        m_activeDataModificationApproval.SourceClientId == sourceClientId &&
                        m_activeDataModificationApproval.RequestId == requestId &&
                        m_activeDataModificationApproval.TransferId == transferId)
                    {
                        CloseDataModificationApprovalDialog();
                    }
                }
                return new object[]
                {
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["resolved"] = resolved
                    }
                };
            }

            var pending = new List<Dictionary<string, object>>();
            if (IsHost && m_dataModification != null)
            {
                foreach (DataModificationApprovalRequest item in
                    m_dataModification.GetPendingApprovals())
                {
                    pending.Add(CreateDataModificationApprovalRecord(item));
                }
            }
            var response = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["pending"] = pending
            };
            if (IsHost)
            {
                // 主机侧授权信息（供无头服务器控制台回答"谁被授权了"）：
                // `trusted` = 世界受信任名单（ScMultiplayerTrustedClients.xml，请求不产生审批）；
                // `clients` = 在线客户端的记录键与是否已授权。
                response["trusted"] = GetTrustedDataModificationIdentities();
                response["clients"] = DescribeDataModificationClientIdentities();
            }
            return new object[] { response };
        }

        internal DataModificationSubmitResult SubmitDataModification(
            DataModificationSubmitRequest request) => m_dataModification?.Submit(request) ??
            new DataModificationSubmitResult
            {
                Code = DataModificationResultCode.Rejected,
                Details = "Data modification is not initialized."
            };

        internal void ReceiveDataModificationMessage(DataModificationMessage message,
            int sourceClientId) => m_dataModification?.Receive(message, sourceClientId);

        internal void RunDataModificationPhase(in ModuleTickContext tickContext) =>
            m_dataModification?.Update(tickContext.Now);

        internal void ResetDataModification()
        {
            CloseDataModificationApprovalDialog();
            m_dataModification?.Reset();
            ResetPlayerAuthorityState();
            ResetPlayerCapabilityState();
        }

        internal object[][] TriggerDataModificationEvent(string eventName, object value) =>
            m_eventBus?.TriggerEvent(eventName, new[] { value }) ?? Array.Empty<object[]>();

        internal void UpdateDataModificationApprovalPrompt()
        {
            if (!IsHost || ScMultiplayerSettings.DataModificationMode !=
                DataModificationPolicy.Default || GameManager.Project == null)
            {
                CloseDataModificationApprovalDialog();
                return;
            }
            if (m_activeDataModificationApprovalDialog != null)
            {
                if (m_dataModification?.IsApprovalPending(
                    m_activeDataModificationApproval) == true &&
                    DialogsManager.Dialogs.Contains(m_activeDataModificationApprovalDialog))
                    return;
                CloseDataModificationApprovalDialog();
            }
            DataModificationApprovalRequest request =
                m_dataModification?.GetNextPendingApproval();
            if (request == null)
                return;
            string source = request.SourcePlayerIndex >= 0
                ? "player " + request.SourcePlayerIndex
                : "client " + request.SourceClientId;
            string size = request.Channel == DataModificationChannel.Fast
                ? request.TotalBytes + " bytes"
                : request.ChunkCount + " chunks / " + request.TotalBytes + " bytes";
            // 三个选项：允许 / 拒绝 / 总是同意该玩家（把该客户端身份加入受信任名单 → 以后自动同意）
            string[] decisions = { "Allow", "Reject", "Always allow this player" };
            string title = request.ModId + " requests " + request.Operation +
                " from " + source + " (" + size + ")" +
                (string.IsNullOrEmpty(request.Summary) ? string.Empty : "\r\n" + request.Summary);
            var dialog = new ListSelectionDialog(
                title,
                decisions,
                60f,
                item => item.ToString(),
                item =>
                {
                    DataModificationApprovalRequest active =
                        m_activeDataModificationApproval;
                    m_activeDataModificationApprovalDialog = null;
                    m_activeDataModificationApproval = null;
                    if (active == null)
                        return;
                    string decision = item?.ToString();
                    if (decisions.Length > 2 && decision == decisions[2])
                        TrustDataModificationClient(active.SourceClientId);
                    m_dataModification?.ResolveApproval(active, decision != decisions[1]);
                });
            m_activeDataModificationApproval = request;
            m_activeDataModificationApprovalDialog = dialog;
            SubsystemPlayers players = GameManager.Project.FindSubsystem<SubsystemPlayers>(false);
            ComponentPlayer localPlayer = players?.ComponentPlayers.FirstOrDefault(player =>
                player?.PlayerData != null &&
                !m_networkPlayerData.Values.Contains(player.PlayerData));
            DialogsManager.ShowDialog(localPlayer?.GuiWidget ?? ScreensManager.RootWidget, dialog);
        }

        private void CloseDataModificationApprovalDialog()
        {
            Dialog dialog = m_activeDataModificationApprovalDialog;
            m_activeDataModificationApprovalDialog = null;
            m_activeDataModificationApproval = null;
            if (dialog != null && DialogsManager.Dialogs.Contains(dialog))
                DialogsManager.HideDialog(dialog);
        }

        internal int ResolveDataModificationPlayerIndex(int clientId) =>
            clientId >= 0 ? playerMappingManager.GetPlayerIndex(clientId) : -1;

        internal void PublishDataModificationResult(DataModificationResult result)
        {
            if (result == null)
                return;
            TriggerDataModificationEvent(DataModificationEvents.Result, result);
            DataModificationTool.RaiseResult(result);
        }

        internal void PublishDataModificationApprovalRequest(
            DataModificationApprovalRequest request)
        {
            if (request != null)
            {
                TriggerDataModificationEvent(DataModificationEvents.ApprovalRequested,
                    CreateDataModificationApprovalRecord(request));
            }
        }

        private static Dictionary<string, object> CreateDataModificationApprovalRecord(
            DataModificationApprovalRequest request)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sourceClientId"] = request.SourceClientId,
                ["sourcePlayerIndex"] = request.SourcePlayerIndex,
                // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:
                // DataModificationApprovalRequest.SourceKey
                // 记录键（账号 userid，或 name:名字）——无头服务器等自动化审批按它判断身份，玩家名不可靠。
                ["sourceKey"] = request.SourceKey ?? string.Empty,
                ["channel"] = request.Channel == DataModificationChannel.Bulk
                    ? "bulk" : "fast",
                ["modId"] = request.ModId,
                ["operation"] = request.Operation,
                ["requestId"] = request.RequestId,
                ["transferId"] = request.TransferId,
                ["chunkCount"] = request.ChunkCount,
                ["totalBytes"] = request.TotalBytes,
                ["receivedTime"] = request.ReceivedTime
            };
        }

        private static bool TryReadApprovalInteger(IDictionary<string, object> values,
            string name, out int result)
        {
            result = 0;
            if (values == null || !values.TryGetValue(name, out object value) || value == null)
                return false;
            try
            {
                result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadApprovalBoolean(IDictionary<string, object> values,
            string name, out bool result)
        {
            result = false;
            if (values == null || !values.TryGetValue(name, out object value) || value == null)
                return false;
            try
            {
                result = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void DisposeDataModification()
        {
            CloseDataModificationApprovalDialog();
            if (m_eventBus != null && m_dataModificationSubmitToken != null)
            {
                m_eventBus.UnsubscribeEvent(m_dataModificationSubmitToken);
                m_dataModificationSubmitToken = null;
            }
            if (m_eventBus != null && m_dataModificationApprovalControlToken != null)
            {
                m_eventBus.UnsubscribeEvent(m_dataModificationApprovalControlToken);
                m_dataModificationApprovalControlToken = null;
            }
            m_dataModification?.Dispose();
            m_dataModification = null;
        }
    }
}

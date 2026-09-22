using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ScMultiplayer
{
    public partial class ScMultiplayer
    {
        private const string TrustedClientsFileName = "ScMultiplayerTrustedClients.xml";

        private readonly HashSet<string> m_trustedDataModificationIdentities =
            new HashSet<string>(StringComparer.Ordinal);
        private string m_trustedClientsWorldDirectory;

        /// <summary>
        /// 联机 mod **自己**能落地的通用数据修改（自定义 operation，不是内置 `ScMP.Player.*`）。
        ///
        /// 这样主机端**不需要安装任何第三方 mod**（例如 GmMod）：主机只负责审批 + 落地 + 分发。
        /// 返回 null 表示"不是我处理的 op"，继续走 `ScMultiplayer.DataModification.Apply` 事件
        /// 交给装了对应 mod 的主机处理。
        /// </summary>
        internal DataModificationApplyResult ApplyHostInternalDataModification(
            DataModificationApplyContext context)
        {
            if (context == null)
                return null;
            if (CellDataOperation.IsCellOperation(context.Operation))
                return ApplyHostCellModification(context);
            // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerWorldControlModification.cs:
            // WorldControlDataOperation.Name
            // 运行时天气（降雨 / 雾气 / 闪电）与时间点不在 `WorldSettings` 里，所以单独一条通用 op。
            if (WorldControlDataOperation.IsWorldControlOperation(context.Operation))
                return ApplyHostWorldControlModification(context);
            if (!WorldSettingsDataOperation.IsWorldSettingsOperation(context.Operation))
                return null;
            if (!IsHost || GameManager.Project == null)
                return DataModificationApplyResult.Reject("An active authoritative host is required.");
            if (context.Channel != DataModificationChannel.Fast || context.ChunkCount != 1 ||
                context.ChunkIndex != 0 || !context.IsFinalChunk)
            {
                return DataModificationApplyResult.Reject(
                    "World settings require one fast single-chunk request.");
            }
            if (!WorldSettingsDataCodec.TryDecode(context.Payload, out var entries,
                out string decodeError))
                return DataModificationApplyResult.Reject(decodeError);

            SubsystemGameInfo gameInfo = GameManager.Project.FindSubsystem<SubsystemGameInfo>(false);
            if (gameInfo == null)
                return DataModificationApplyResult.Reject("No world is loaded on the host.");
            if (!WorldSettingsFields.TryApply(gameInfo.WorldSettings, entries, out string summary,
                out string applyError))
            {
                return DataModificationApplyResult.Reject(
                    applyError ?? "The world settings change was rejected.");
            }

            Log.Information("[ScMP] Host applied world settings from client " +
                context.SourceClientId + ": " + summary);
            PublishServerAudit("dm.world", context.SourceClientId,
                "operation=" + context.Operation + " " + summary + " mod=" +
                NormalizePlayerAdministrationAuditValue(context.ModId));
            // 落地即生效：主机下一次世界信息广播（2Hz）会把整份 WorldSettings 快照发给所有客户端，
            // 由客户端自己应用（分发完全在主机侧，第三方 mod 不参与）。
            return DataModificationApplyResult.Success("world settings: " + summary);
        }

        /// <summary>
        /// 主机落地「地图方块」修改：直接改权威地形，然后走既有地形同步链广播给所有客户端。
        /// 光照位由主机按原地形保留（客户端不能指定光源）。
        /// </summary>
        private DataModificationApplyResult ApplyHostCellModification(
            DataModificationApplyContext context)
        {
            if (!IsHost || GameManager.Project == null)
                return DataModificationApplyResult.Reject("An active authoritative host is required.");
            if (context.Channel != DataModificationChannel.Fast || context.ChunkCount != 1 ||
                context.ChunkIndex != 0 || !context.IsFinalChunk)
            {
                return DataModificationApplyResult.Reject(
                    "Cell edits require one fast single-chunk request.");
            }
            if (!CellDataCodec.TryDecode(context.Payload, out var edits, out string decodeError))
                return DataModificationApplyResult.Reject(decodeError);

            SubsystemTerrain terrain = GameManager.Project.FindSubsystem<SubsystemTerrain>(false);
            if (terrain == null)
                return DataModificationApplyResult.Reject("No terrain is loaded on the host.");
            int applied = 0;
            foreach (CellDataCodec.CellEdit edit in edits)
            {
                int existing = terrain.Terrain.GetCellValue(edit.X, edit.Y, edit.Z);
                int light = Terrain.ExtractLight(existing);
                int value = Terrain.ReplaceLight(
                    Terrain.MakeBlockValue(edit.Contents, 0, edit.Data), light);
                terrain.ChangeCell(edit.X, edit.Y, edit.Z, value);
                applied++;
            }
            terrain.TerrainUpdater.RequestSynchronousUpdate();
            (terrain as SuSubsystemTerrain)?.FlushHostModifiedCellClosureForNetworkAction();

            string summary = applied + " cell edit(s), first=" + edits[0].X + "," + edits[0].Y + "," +
                edits[0].Z + " contents=" + edits[0].Contents;
            Log.Information("[ScMP] Host applied cell edits from client " + context.SourceClientId +
                ": " + summary);
            PublishServerAudit("dm.cells", context.SourceClientId,
                "operation=" + context.Operation + " " + summary + " mod=" +
                NormalizePlayerAdministrationAuditValue(context.ModId));
            return DataModificationApplyResult.Success(summary);
        }

        /// <summary>审批弹窗用的请求摘要（改了哪些设置/哪些方块，目标是谁），看不懂的 op 返回 null。</summary>
        internal static string DescribeDataModificationRequestSummary(string operation, byte[] payload)
        {
            if (CellDataOperation.IsCellOperation(operation))
            {
                if (payload == null || payload.Length == 0)
                    return null;
                if (!CellDataCodec.TryDecode(payload, out var cells, out _))
                    return "(unreadable payload)";
                string head = string.Join("; ", cells.Take(3).Select(edit =>
                    edit.X + "," + edit.Y + "," + edit.Z + "=" + edit.Contents));
                return head + (cells.Count > 3 ? " (+" + (cells.Count - 3) + " more)" : string.Empty);
            }
            if (DataModificationOperationNames.IsBuiltIn(operation))
            {
                // 内置角色操作：把"改谁 + 改什么"写进摘要，主机能看到跨玩家目标再决定是否同意。
                if (payload == null || payload.Length == 0)
                    return null;
                if (!PlayerDataModificationCodec.TryDecode(payload,
                    out PlayerDataModificationRequest request, out _))
                    return "(unreadable payload)";
                string target = request.TargetClientId >= 0
                    ? "target=client " + request.TargetClientId
                    : "target=self";
                return operation + " (" + target + ")" +
                    (request.SetAbsoluteLevel ? " level=" + request.Level : string.Empty) +
                    (request.SetAbsoluteLevel ? string.Empty
                        : request.Amount != 0f ? " amount=" + request.Amount : string.Empty) +
                    (request.ItemValue != 0 ? " item=" + request.ItemValue + " x" +
                        request.ItemCount : string.Empty) +
                    DescribeVitalsAndConditionRequest(operation, request);
            }
            // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerWorldControlModification.cs:
            // WorldControlDataOperation.Name
            if (WorldControlDataOperation.IsWorldControlOperation(operation))
            {
                if (payload == null || payload.Length == 0)
                    return null;
                if (!WorldSettingsDataCodec.TryDecode(payload, out var actions, out _))
                    return "(unreadable payload)";
                return string.Join(", ",
                    actions.Select(entry => entry.Key + "=" + entry.Value));
            }
            if (!WorldSettingsDataOperation.IsWorldSettingsOperation(operation))
                return null;
            if (payload == null || payload.Length == 0)
                return null;
            if (!WorldSettingsDataCodec.TryDecode(payload, out var entries, out _))
                return "(unreadable payload)";
            return string.Join(", ", entries.Take(4).Select(entry => entry.Key + "=" + entry.Value)) +
                (entries.Count > 4 ? " (+" + (entries.Count - 4) + " more)" : string.Empty);
        }

        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:VitalsField
        // Source: Mod/ScMultiplayer/DataModification/DataModificationContracts.cs:ConditionKind
        // 新 op（SetVitals / SetCondition）在审批弹窗里的"改了什么"：不写出来的话主机只看到 op 名。
        private static string DescribeVitalsAndConditionRequest(string operation,
            PlayerDataModificationRequest request)
        {
            if (request == null)
                return string.Empty;
            var parts = new List<string>();
            if ((request.Vitals & VitalsField.Food) != 0)
                parts.Add("food=" + request.VitalsFood);
            if ((request.Vitals & VitalsField.Stamina) != 0)
                parts.Add("stamina=" + request.VitalsStamina);
            if ((request.Vitals & VitalsField.Sleep) != 0)
                parts.Add("sleep=" + request.VitalsSleep);
            if ((request.Vitals & VitalsField.Temperature) != 0)
                parts.Add("temperature=" + request.VitalsTemperature);
            if ((request.Vitals & VitalsField.Wetness) != 0)
                parts.Add("wetness=" + request.VitalsWetness);
            if (string.Equals(operation, DataModificationOperationNames.SetCondition,
                StringComparison.Ordinal))
            {
                parts.Add("condition=" +
                    (request.Condition == ConditionKind.None ? "all" : request.Condition.ToString()) +
                    (request.ConditionMode == ConditionAction.Apply ? "/apply" : "/clear") +
                    (request.ConditionDuration > 0f
                        ? " duration=" + request.ConditionDuration
                        : string.Empty));
            }
            return parts.Count == 0 ? string.Empty : " " + string.Join(" ", parts);
        }

        // ================================================================
        // 受信任客户端（身份 = 客户端的 UserManager.ActiveUser.UniqueId = 角色记录键）
        // ================================================================

        /// <summary>
        /// 在线玩家列表（供第三方 GM 工具做目标选择：跨玩家操作需要目标 clientId）。
        /// 主机侧 = 本地玩家(clientId 0) + 各网络化身；客户端侧 = 网络化身（含主机 0）+ 自己。
        /// </summary>
        internal void DescribeOnlinePlayersInto(List<Dictionary<string, object>> result)
        {
            if (result == null)
                return;
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(false);
            if (players == null)
                return;
            if (IsHost)
            {
                ComponentPlayer local = players.ComponentPlayers.FirstOrDefault(item =>
                    item?.PlayerData != null && !m_networkPlayerData.Values.Contains(item.PlayerData));
                if (local?.PlayerData != null)
                    result.Add(CreateOnlinePlayerRecord(0, local.PlayerData, isSelf: true));
                foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
                {
                    if (item.Value != null)
                        result.Add(CreateOnlinePlayerRecord(item.Key, item.Value, isSelf: false));
                }
                return;
            }
            foreach (KeyValuePair<int, PlayerData> item in m_networkPlayerData)
            {
                if (item.Value != null)
                    result.Add(CreateOnlinePlayerRecord(item.Key, item.Value, isSelf: false));
            }
            int selfClientId = client?.ClientID ?? -1;
            PlayerData selfData = m_localReplacementPlayerData;
            if (selfData != null && selfClientId >= 0)
                result.Add(CreateOnlinePlayerRecord(selfClientId, selfData, isSelf: true));
        }

        private static Dictionary<string, object> CreateOnlinePlayerRecord(int clientId,
            PlayerData playerData, bool isSelf) =>
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["clientId"] = clientId,
                ["name"] = playerData?.Name ?? "Player",
                ["playerIndex"] = playerData?.PlayerIndex ?? -1,
                ["level"] = playerData?.Level ?? 1f,
                ["isSelf"] = isSelf,
                ["isHost"] = clientId == 0
            };

        /// <summary>
        /// 该客户端的记录键（= `UserManager.ActiveUser.UniqueId` = 账号 userid；无身份时为 `name:名字`）。
        /// 名字可以随便改、这个键不能改，所以主机侧授权一律按它判断（审批事件也携带它）。
        /// </summary>
        internal string GetDataModificationClientKey(int clientId)
        {
            if (clientId < 0)
                return string.Empty;
            return m_clientRecordKeys.TryGetValue(clientId, out string key) && key != null
                ? key
                : string.Empty;
        }

        /// <summary>
        /// 当前世界的受信任身份（`ScMultiplayerTrustedClients.xml` 的内容）。
        /// 这些身份的 DM 请求**连审批请求都不会产生**，主机直接落地；无头服务器控制台用它显示"谁被授权了"。
        /// </summary>
        internal List<string> GetTrustedDataModificationIdentities()
        {
            var result = new List<string>();
            if (!IsHost)
                return result;
            EnsureTrustedDataModificationIdentitiesLoaded();
            result.AddRange(m_trustedDataModificationIdentities);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 在线客户端的记录键 + 是否已授权（无头服务器控制台用它显示"谁被授权了、谁还要问"）。
        /// 主机自己（clientId 0）不在此列。
        /// </summary>
        internal List<Dictionary<string, object>> DescribeDataModificationClientIdentities()
        {
            var result = new List<Dictionary<string, object>>();
            if (!IsHost)
                return result;
            EnsureTrustedDataModificationIdentitiesLoaded();
            foreach (KeyValuePair<int, string> item in m_clientRecordKeys.OrderBy(pair => pair.Key))
            {
                if (item.Key <= 0 || string.IsNullOrWhiteSpace(item.Value))
                    continue;
                result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["clientId"] = item.Key,
                    ["key"] = item.Value,
                    ["name"] = m_networkPlayerData.TryGetValue(item.Key, out PlayerData data) &&
                        data != null ? data.Name ?? string.Empty : string.Empty,
                    ["trusted"] = m_trustedDataModificationIdentities.Contains(item.Value)
                });
            }
            return result;
        }

        /// <summary>该客户端身份是否已被主机授权（授权后其数据修改请求自动同意，不弹窗）。</summary>
        internal bool IsTrustedDataModificationClient(int clientId)
        {
            if (clientId < 0)
                return false;
            EnsureTrustedDataModificationIdentitiesLoaded();
            return m_clientRecordKeys.TryGetValue(clientId, out string identity) &&
                m_trustedDataModificationIdentities.Contains(identity);
        }

        /// <summary>把该客户端身份加入受信任名单（主机审批弹窗的"总是同意该玩家"）。</summary>
        internal bool TrustDataModificationClient(int clientId)
        {
            if (!IsHost || clientId < 0 ||
                !m_clientRecordKeys.TryGetValue(clientId, out string identity) ||
                string.IsNullOrWhiteSpace(identity))
                return false;
            return TrustDataModificationIdentity(identity, clientId);
        }

        // Source: Mod/ScMultiplayer/DataModification/ScMultiplayerDataModificationRuntime.cs:
        // ScMultiplayer.HandleDataModificationApprovalControl（operation = "trust"）
        // 无头服务器控制台在审批时拿到的可能只有记录键（`sourceKey`）：玩家已经离线时
        // `m_clientRecordKeys` 里已经没有该 clientId，所以必须支持按身份直接授权。
        internal bool TrustDataModificationIdentity(string identity, int clientId = -1)
        {
            if (!IsHost || string.IsNullOrWhiteSpace(identity))
                return false;
            string normalized = identity.Trim();
            EnsureTrustedDataModificationIdentitiesLoaded();
            string existing = m_trustedDataModificationIdentities.FirstOrDefault(value =>
                string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return true;
            m_trustedDataModificationIdentities.Add(normalized);
            SaveTrustedDataModificationIdentities();
            Log.Information("[ScMP] " + normalized +
                (clientId >= 0 ? " (client " + clientId + ")" : string.Empty) +
                " is now a trusted data modification client");
            return true;
        }

        /// <summary>
        /// 取消该身份的受信任授权（无头服务器控制台 `Authorised players` 的"取消授权"）。
        /// 内存集合就是真相源（`IsTrustedDataModificationClient` 每次请求都查它），写盘只是持久化，
        /// 所以取消**立即生效**。
        /// </summary>
        internal bool UntrustDataModificationIdentity(string identity)
        {
            if (!IsHost || string.IsNullOrWhiteSpace(identity))
                return false;
            string normalized = identity.Trim();
            EnsureTrustedDataModificationIdentitiesLoaded();
            string existing = m_trustedDataModificationIdentities.FirstOrDefault(value =>
                string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing == null || !m_trustedDataModificationIdentities.Remove(existing))
                return false;
            SaveTrustedDataModificationIdentities();
            Log.Information("[ScMP] " + existing +
                " is no longer a trusted data modification client");
            return true;
        }

        // Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:EnsurePlayerRecordsLoaded
        // 与角色记录一样，放世界目录里的旁挂文件：跨房间/跨重启保留，且不进 Project.xml。
        private void EnsureTrustedDataModificationIdentitiesLoaded()
        {
            if (!IsHost)
                return;
            string directory = GameManager.Project?
                .FindSubsystem<SubsystemGameInfo>(false)?.DirectoryName;
            if (string.IsNullOrEmpty(directory) ||
                string.Equals(directory, m_trustedClientsWorldDirectory,
                    StringComparison.OrdinalIgnoreCase))
                return;

            m_trustedDataModificationIdentities.Clear();
            m_trustedClientsWorldDirectory = directory;
            string path = Storage.CombinePaths(directory, TrustedClientsFileName);
            if (!Storage.FileExists(path))
                return;
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                foreach (XElement element in document.Root?.Elements("Trusted") ??
                    Enumerable.Empty<XElement>())
                {
                    string identity = (string)element.Attribute("Identity");
                    if (!string.IsNullOrWhiteSpace(identity))
                        m_trustedDataModificationIdentities.Add(identity.Trim());
                }
                Log.Information("[ScMP] Loaded " + m_trustedDataModificationIdentities.Count +
                    " trusted data modification identities");
            }
            catch (Exception ex)
            {
                Log.Error("[ScMP] Failed to load trusted clients: " + ex.Message);
            }
        }

        private void SaveTrustedDataModificationIdentities()
        {
            if (!IsHost || string.IsNullOrEmpty(m_trustedClientsWorldDirectory))
                return;
            try
            {
                var root = new XElement("ScMultiplayerTrustedClients",
                    new XAttribute("Version", 1));
                foreach (string identity in m_trustedDataModificationIdentities.OrderBy(
                    value => value, StringComparer.Ordinal))
                {
                    root.Add(new XElement("Trusted", new XAttribute("Identity", identity)));
                }
                string path = Storage.CombinePaths(m_trustedClientsWorldDirectory,
                    TrustedClientsFileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
            }
            catch (Exception ex)
            {
                Log.Error("[ScMP] Failed to save trusted clients: " + ex.Message);
            }
        }
    }
}

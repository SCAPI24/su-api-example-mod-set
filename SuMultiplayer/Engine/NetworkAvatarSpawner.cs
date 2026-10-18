using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;
using SuMultiplayer.Terrain;
using TemplatesDatabase;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 联机角色（网络化身）的**引擎动作**：把旧实现
    /// <c>ScMultiplayer.CreateNetworkPlayer</c> 的建实体流程原样搬过来。
    ///
    /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:169-346
    ///         （CreateNetworkPlayer：建 PlayerData → 挑空位 PlayerIndex → AddPlayerData →
    ///          CreateEntity → 摆位置 → 摘 Updateable → m_entities.Remove → 回填 → 摘 GameWidget →
    ///          transition Playing；含失败回滚）
    ///
    /// 三条必须原样保留的关键点：
    ///   1. <c>Project.m_entities.Remove(entity)</c> —— 网络角色**不属于这个世界**，
    ///      从项目实体表摘掉后 SaveProject 就写不到它，Project.xml 永远干净；
    ///   2. 挑空位后先把 <c>m_nextPlayerIndex</c> 压到该槽位再 <c>AddPlayerData</c>
    ///      （引擎的 AddPlayerData 用 m_nextPlayerIndex++ 发号），随后向上修复计数器；
    ///   3. 失败必须回滚（从 m_playersData 摘掉、删实体、Dispose），并且把原因交给调用方。
    ///
    /// 本类**无静态状态**：引擎反射入口（IModParentField / IModParentMethod）由组合根装配进来。
    /// </summary>
    public sealed class NetworkAvatarSpawner
    {
        private readonly IModParentField m_parentField;
        private readonly IModParentMethod m_parentMethod;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        /// <summary>clientId → 网络化身的 PlayerData（只在本机存在，不落 Project.xml）。</summary>
        private readonly Dictionary<int, PlayerData> m_avatars = new Dictionary<int, PlayerData>();
        /// <summary>clientId → 该化身的角色记录键（身份键，绝不用 index 拼）。</summary>
        private readonly Dictionary<int, string> m_clientRecordKeys = new Dictionary<int, string>();
        private readonly HashSet<int> m_creating = new HashSet<int>();
        /// <summary>
        /// 兴趣半径上限（单位 = 区块）。
        /// Source: Mod/ScMultiplayer/Core/ScMultiplayerRuntimeState.cs:917（MaximumTerrainInterestRadius）
        /// </summary>
        private const int MaximumTerrainInterestRadius = 32;
        /// <summary>
        /// 主机侧：clientId → 该玩家端**上报**的兴趣半径（不是主机自己算的）。
        /// Source: Mod/ScMultiplayer/Core/ScMultiplayerRuntimeState.cs:519（m_hostTerrainReportedInterestRadii）
        /// </summary>
        private readonly Dictionary<int, int> m_hostTerrainReportedInterestRadii =
            new Dictionary<int, int>();
        /// <summary>
        /// 主机侧：clientId → 实际交给引擎的地形更新位置（区块 / 半径 / 内容距离 / 移动次数）。
        /// 状态行的 <c>hostUpdateLocations</c> 与 <c>updateLocationMoves</c> 都读它；
        /// 几何与账本在 <see cref="HostTerrainLocationTable"/>（纯托管，自检可直接断言）。
        /// </summary>
        private readonly HostTerrainLocationTable m_hostTerrainLocations =
            new HostTerrainLocationTable();
        /// <summary>clientId → 该联机角色"离自己的更新位置 &gt; 8 区块"的起始时刻（毫秒，Time.RealTime 口径）。</summary>
        private readonly Dictionary<int, long> m_updateLocationDriftSinceMs =
            new Dictionary<int, long>();
        /// <summary>限流日志：key → 上次写入的毫秒（Time.RealTime 口径）。</summary>
        private readonly Dictionary<string, long> m_lastRateLimitedLogMs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        /// <summary>
        /// 联机角色与它的地形更新位置相差多少区块才告警。
        /// 正常路径每 tick 都会把位置刷成角色的 ComponentBody.Position（相距 0）；
        /// 只有更新位置没挂上 / 没刷新时才会累积到 8 区块 —— 那时主机就不会在角色周围加载地形。
        /// </summary>
        private const int UpdateLocationDriftChunkThreshold = 8;
        /// <summary>偏离持续多久才告警（毫秒）；用户口径 2 秒。</summary>
        private const long UpdateLocationDriftWarningMs = 2000;
        /// <summary>同一条限流告警 / 移动日志的最小间隔（毫秒）。</summary>
        private const long UpdateLocationLogIntervalMs = 5000;
        /// <summary>持续性问题 / 一次性事实只记一条（原因照实写，绝不静默）。</summary>
        private readonly HashSet<string> m_reportedOnce = new HashSet<string>(StringComparer.Ordinal);

        public NetworkAvatarSpawner(IModParentField parentField, IModParentMethod parentMethod,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_parentField = parentField ?? throw new ArgumentNullException(nameof(parentField));
            m_parentMethod = parentMethod ?? throw new ArgumentNullException(nameof(parentMethod));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-avatar" : ownerId;
        }

        public int Count => m_avatars.Count;

        public IEnumerable<int> ClientIds => m_avatars.Keys;

        /// <summary>
        /// 累计"联机角色的地形更新位置换到另一个区块"的次数（实机排查"主机有没有跟着客户端移动"）。
        /// 只统计**跨区块**的移动：引擎自己的 <c>SetUpdateLocation</c> 对 &lt; 8 格的移动本来就不重排。
        /// </summary>
        public long UpdateLocationMoves => m_hostTerrainLocations.Moves;

        /// <summary>当前挂着的联机角色地形更新位置条数（应等于主机的联机角色数）。</summary>
        public int HostUpdateLocationCount => m_hostTerrainLocations.Count;

        /// <summary>
        /// 状态行用：每个 clientId 的地形更新位置（区块 + 半径 + 内容距离 + 已移动次数）。
        /// 空表返回 <c>none</c>（主机一个都没挂时一眼可见）。
        /// </summary>
        public string DescribeHostUpdateLocations() => m_hostTerrainLocations.Describe();

        public bool TryGet(int clientId, out PlayerData playerData) =>
            m_avatars.TryGetValue(clientId, out playerData);

        /// <summary>
        /// 主机：某个玩家端的联机角色在世界里的**真实位置**（该角色实体的 ComponentBody.Position）。
        /// 实时地图用它显示客户端坐标 —— 这是主机手里关于"客户端角色在哪"的唯一权威事实，
        /// 绝不按区块中心猜（区块中心是 16 格粒度，也拿不到 Y）。
        /// 读不到就返回 false 并写明原因（角色实体还没建好 / 没有 ComponentPlayer / 没有 ComponentBody），
        /// 由调用方明说"坐标未知"。
        /// </summary>
        public bool TryGetAvatarCell(int clientId, out Vector3 position, out string reason)
        {
            position = Vector3.Zero;
            reason = null;
            if (!m_avatars.TryGetValue(clientId, out PlayerData playerData) || playerData == null)
            {
                reason = "clientId=" + clientId + " 还没有联机角色（角色实体没建起来或已移除）";
                return false;
            }
            ComponentPlayer component = playerData.ComponentPlayer;
            if (component == null)
            {
                reason = "clientId=" + clientId + " 的联机角色还没有 ComponentPlayer";
                return false;
            }
            if (component.ComponentBody == null)
            {
                reason = "clientId=" + clientId + " 的联机角色还没有 ComponentBody（位置读不到）";
                return false;
            }
            position = component.ComponentBody.Position;
            return true;
        }

        public bool TryGetRecordKey(int clientId, out string recordKey) =>
            m_clientRecordKeys.TryGetValue(clientId, out recordKey);

        // ------------------------------------------------------- 资料（名字 / 性别 / 皮肤 / 衣服）

        /// <summary>
        /// 主机：抓本机玩家（PlayersData 里不是联机化身的那一条）的真实资料。
        /// 名字取 PlayerData.Name（角色创建界面写进去的真实名字，不是引擎固定的 "Android User"）。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:376-467
        ///         （CapturePlayerRecord 的 Name / PlayerClass / SkinName / Clothes）
        /// </summary>
        public bool TryCaptureLocalProfile(out PlayerProfileSnapshot profile, out string reason)
        {
            profile = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（主机还没进入世界）";
                return false;
            }
            if (!TryGetLocalPlayer(project, out PlayerData localPlayer, out reason)) return false;
            profile = CaptureProfile(localPlayer, out reason);
            return profile != null;
        }

        /// <summary>
        /// 抓一个 PlayerData 的资料（名字 / 性别 / 皮肤 / 衣服）。外观字段取引擎真实值：
        /// <c>PlayerClass</c>、<c>CharacterSkinName</c>、<c>ComponentClothing</c> 的 4 个槽位。
        /// </summary>
        public PlayerProfileSnapshot CaptureProfile(PlayerData playerData, out string reason)
        {
            reason = null;
            if (playerData == null)
            {
                reason = "没有 PlayerData";
                return null;
            }
            string name = (playerData.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                reason = "PlayerData.Name 为空（角色还没有名字）";
                return null;
            }
            string skin = (playerData.CharacterSkinName ?? string.Empty).Trim();
            if (skin.Length == 0)
            {
                reason = "PlayerData.CharacterSkinName 为空（拿不到皮肤）";
                return null;
            }
            return new PlayerProfileSnapshot
            {
                Name = name,
                Appearance = new PlayerAppearance
                {
                    Sex = ToPlayerSex(playerData.PlayerClass),
                    SkinName = skin,
                    Clothes = CaptureClothes(playerData.ComponentPlayer)
                }
            };
        }

        /// <summary>
        /// 把一个远端化身的外观更新成资料里的值。
        ///   · 名字 / 皮肤 / 衣服 → 就地更新（引擎的 ComponentClothing 会在 CharacterSkinName
        ///     变化时重新加载贴图，见 Survivalcraft/Game/ComponentClothing.cs:497-500）；
        ///   · **性别变了** → 不能改已存在 PlayerData 的 PlayerClass（引擎属性 setter 会抛），
        ///     必须摘掉重建：返回 false + <paramref name="mustRecreate"/>=true，由调用方重建。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:825-845
        ///         （PlayerClass 变化 → RemoveNetworkPlayer + CreateNetworkPlayer；否则就地写 Name/Skin/Clothes）
        /// </summary>
        public bool TryApplyRemoteProfile(int clientId, PlayerProfileSnapshot profile,
            out bool mustRecreate, out string reason)
        {
            mustRecreate = false;
            reason = null;
            if (profile == null || !profile.HasName)
            {
                reason = "资料为空或没有名字";
                return false;
            }
            if (!m_avatars.TryGetValue(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer == null)
            {
                reason = "本机没有 clientId=" + clientId + " 的联机角色（或角色实体还没建好）";
                return false;
            }
            PlayerClass playerClass = ToPlayerClass(profile.Appearance?.Sex ?? PlayerSex.Male);
            if (playerData.PlayerClass != playerClass)
            {
                mustRecreate = true;
                reason = "性别从 " + playerData.PlayerClass + " 变成 " + playerClass +
                    "：已存在 PlayerData 的 PlayerClass 不可改，必须摘掉重建（旧实现同样重建）";
                return false;
            }
            playerData.Name = profile.Name;
            if (profile.Appearance != null && profile.Appearance.HasSkinName)
                playerData.CharacterSkinName = profile.Appearance.SkinName;
            ApplyClothes(playerData.ComponentPlayer, profile.Appearance?.Clothes);
            return true;
        }

        /// <summary>
        /// 客户端：把主机分配的资料应用到**本机玩家**（不是远端化身）。
        /// 若本机 PlayerData 已经存在（下载世界带来的角色）且性别与资料不同，
        /// 走旧实现的"替换本机 PlayerData"链：移除 → 用同样 PlayerIndex 重建 → 立即以 Respawn 出生。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:1405-1469
        ///         （ReplaceLocalPlayerData）
        /// 返回 true = 本机玩家已经是资料里的样子。
        /// </summary>
        public bool TryApplyLocalProfile(PlayerProfileSnapshot profile, out bool replaced,
            out string reason)
        {
            replaced = false;
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（客户端还没进入世界）";
                return false;
            }
            if (profile == null || !profile.HasName)
            {
                reason = "资料为空或没有名字";
                return false;
            }
            if (!TryGetLocalPlayer(project, out PlayerData localPlayer, out reason)) return false;
            PlayerClass targetClass = ToPlayerClass(profile.Appearance?.Sex ?? PlayerSex.Male);
            if (localPlayer.PlayerClass != targetClass)
            {
                if (!ReplaceLocalPlayerData(project, localPlayer, profile, out PlayerData replacement,
                    out reason))
                    return false;
                localPlayer = replacement;
                replaced = true;
            }
            localPlayer.Name = profile.Name;
            if (profile.Appearance != null && profile.Appearance.HasSkinName)
                localPlayer.CharacterSkinName = profile.Appearance.SkinName;
            ApplyClothes(localPlayer.ComponentPlayer, profile.Appearance?.Clothes);
            return true;
        }

        /// <summary>该 PlayerData 是不是本机登记过的联机角色（网络化身）。</summary>
        public bool Contains(PlayerData playerData) =>
            playerData != null && m_avatars.Values.Any(item => ReferenceEquals(item, playerData));

        /// <summary>
        /// 建一个联机角色实体。
        /// </summary>
        /// <param name="clientId">对端在本机通道里的编号（**只用于本机索引，不当身份**）。</param>
        /// <param name="recordKey">身份键（由 PlayerIdentity.RecordKey 算出）。</param>
        /// <param name="record">该身份的角色记录；没有记录时按引擎默认值建（旧实现同样支持 record == null）。</param>
        /// <param name="isHost">本端是不是主机（影响是否摘 Updateable、是否走首次出生流程）。</param>
        public bool TryCreate(int clientId, string requestedName, string recordKey,
            NetworkPlayerRecord record, bool isHost, out string failureReason)
        {
            failureReason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                failureReason = "世界上没有加载中的 Project（还没进入世界）";
                return false;
            }
            if (m_avatars.ContainsKey(clientId) || !m_creating.Add(clientId))
            {
                failureReason = "clientId " + clientId + " 的联机角色已经存在或正在创建";
                return false;
            }

            SubsystemPlayers players = null;
            PlayerData playerData = null;
            Entity entity = null;
            try
            {
                players = project.FindSubsystem<SubsystemPlayers>(throwOnError: true);
                PlayerData hostPlayer = players.PlayersData.FirstOrDefault();
                string playerName = string.IsNullOrWhiteSpace(requestedName)
                    ? ("NetPlayer" + clientId)
                    : requestedName.Trim();
                if (playerName.Length > 14) playerName = playerName.Substring(0, 14);
                playerData = new PlayerData(project)
                {
                    Name = record?.Name ?? playerName,
                    PlayerClass = record?.PlayerClass ?? PlayerClass.Male,
                    Level = record?.Level ?? PlayerRecordCodec.DefaultLevel,
                    InputDevice = WidgetInputDevice.None,
                    // record?.SpawnPosition != Vector3.Zero 会提升成可空比较，而 null != Vector3.Zero 为真：
                    // 没有记录时（客户端刚加入的头几帧）会取到 record.SpawnPosition 并抛空引用。
                    // 判定必须落在 record 本身上。
                    // Source: ScMultiplayerWorldObjectHandlers.cs:217-225
                    SpawnPosition = record != null && record.SpawnPosition != Vector3.Zero
                        ? record.SpawnPosition
                        : (players.GlobalSpawnPosition != Vector3.Zero
                            ? players.GlobalSpawnPosition
                            : (hostPlayer?.ComponentPlayer?.ComponentBody.Position ?? record?.Position ?? Vector3.Zero))
                };
                if (!string.IsNullOrEmpty(record?.SkinName)) playerData.CharacterSkinName = record.SkinName;

                int freePlayerIndex = FindAvailablePlayerIndex(players);
                if (freePlayerIndex < 0 || freePlayerIndex >= SubsystemGameWidgets.MaxGameWidgets)
                {
                    throw new InvalidOperationException("没有空闲的引擎玩家槽位（0.." +
                        (SubsystemGameWidgets.MaxGameWidgets - 1) + "），无法创建联机角色");
                }
                if (players.PlayersData.Any(item => item.PlayerIndex == freePlayerIndex) ||
                    m_avatars.Values.Any(item => item.PlayerIndex == freePlayerIndex))
                {
                    throw new InvalidOperationException("挑中的远程玩家槽位 " + freePlayerIndex + " 已被占用");
                }
                // 引擎发号顺序：AddPlayerData 里 PlayerIndex = m_nextPlayerIndex++，
                // 所以先把计数器压到目标槽位，再由 AddPlayerData 取出这个号。
                // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.AddPlayerData
                m_parentField.ModifyParentField(players, "m_nextPlayerIndex", freePlayerIndex, typeof(SubsystemPlayers));
                players.AddPlayerData(playerData);
                RepairEnginePlayerIndexCounter(players);

                ValuesDictionary overrides = new ValuesDictionary
                {
                    { "Player", new ValuesDictionary { { "PlayerIndex", playerData.PlayerIndex } } },
                    { "Intro", new ValuesDictionary { { "PlayIntro", false } } }
                };

                bool initialSpawn = isHost && record != null && !record.HasReceivedInitialItems;
                if (initialSpawn)
                {
                    InvokeInitialPlayerSpawn(playerData, record.Position);
                    entity = playerData.ComponentPlayer?.Entity ??
                        throw new InvalidOperationException("网络角色首次出生失败：PlayerData 没有 ComponentPlayer");
                    record.HasReceivedInitialItems = true;
                }
                else
                {
                    entity = DatabaseManager.CreateEntity(project, playerData.GetEntityTemplateName(),
                        overrides, throwIfNotFound: true);
                    entity.FindComponent<ComponentBody>(throwOnError: true).Position =
                        record?.Position ?? (playerData.SpawnPosition + new Vector3(1f, 0f, 0f));
                    project.AddEntity(entity);
                }

                SubsystemUpdate subsystemUpdate = project.FindSubsystem<SubsystemUpdate>(throwOnError: true);
                foreach (IUpdateable updateable in entity.FindComponents<IUpdateable>())
                {
                    // 客户端的"主机角色"是**远端**角色：本机不跑它的输入/移动/挖掘，
                    // 否则本地模拟会和主机的权威位置打架。
                    // Source: ScMultiplayerWorldObjectHandlers.cs:264-271
                    if (!isHost && (updateable is ComponentPlayer || updateable is ComponentInput ||
                        updateable is ComponentLocomotion || updateable is ComponentMiner))
                    {
                        subsystemUpdate.RemoveUpdateable(updateable);
                    }
                }

                // ★ 关键：把网络角色的实体从 Project 的实体表里摘掉。
                // SaveProject 只写 m_entities 里的实体，所以联机角色**永远不会进 Project.xml**
                // （角色数据只存在于 SuMultiplayerPlayers.xml）。
                // Source: ScMultiplayerWorldObjectHandlers.cs:272
                m_parentField.GetParentField<Dictionary<Entity, bool>>(project, "m_entities", typeof(Project))
                    .Remove(entity);

                IInventory inventory = playerData.ComponentPlayer?.ComponentMiner?.Inventory;
                ConfigureNetworkPlayerInventory(inventory);
                RestorePlayerRecordInventory(inventory, record);
                RestorePlayerRecordCrafting(
                    playerData.ComponentPlayer?.Entity?.FindComponent<ComponentCraftingTable>(), record);
                ApplyClothes(playerData.ComponentPlayer, record?.Clothes);
                if (record != null)
                {
                    ApplyAuthoritativePlayerStats(playerData.ComponentPlayer, record.Health, record.Air,
                        record.Food, record.Stamina, record.Sleep, record.Temperature, record.Wetness,
                        record.Level);
                    ApplyPlayerRecordState(playerData.ComponentPlayer, record);
                }

                m_parentField.GetParentField<StateMachine>(playerData, "m_stateMachine", typeof(PlayerData))
                    .TransitionTo("Playing");

                // 远端角色没有自己的界面：把它的 GameWidget 从引擎界面子系统里摘掉，
                // 并把 PlayerData 从"本机玩家列表"移到"角色列表"。
                // Source: ScMultiplayerWorldObjectHandlers.cs:291-300
                SubsystemGameWidgets gameWidgets = project.FindSubsystem<SubsystemGameWidgets>(throwOnError: true);
                GameWidget networkGameWidget = playerData.GameWidget;
                m_parentMethod.GetInstanceMethodInfo(typeof(SubsystemGameWidgets), "RemoveGameWidget",
                    new[] { typeof(GameWidget) }).Invoke(gameWidgets, new object[] { networkGameWidget });
                m_parentField.GetParentField<List<PlayerData>>(players, "m_playersData", typeof(SubsystemPlayers))
                    .Remove(playerData);
                List<ComponentPlayer> componentPlayers = m_parentField.GetParentField<List<ComponentPlayer>>(
                    players, "m_componentPlayers", typeof(SubsystemPlayers));
                if (playerData.ComponentPlayer != null && !componentPlayers.Contains(playerData.ComponentPlayer))
                    componentPlayers.Add(playerData.ComponentPlayer);

                m_avatars.Add(clientId, playerData);
                m_clientRecordKeys[clientId] = recordKey;
                // 新建（或重连后重建）的角色不沿用上一次的位置插值状态：第一份快照必须直接对齐
                // （旧实现同样是 RemoveNetworkPlayer 清、CreateNetworkPlayer 后从零开始）。
                ForgetRemotePosition(clientId);
                Log("已创建联机角色：clientId=" + clientId + " · 记录键=" +
                    PlayerIdentity.Describe(recordKey) + " · PlayerIndex=" + playerData.PlayerIndex);
                return true;
            }
            catch (Exception ex)
            {
                // 回滚（照旧实现）：不能留下半个角色。
                // Source: ScMultiplayerWorldObjectHandlers.cs:322-338
                try
                {
                    if (players != null)
                    {
                        List<PlayerData> playerList = m_parentField.GetParentField<List<PlayerData>>(
                            players, "m_playersData", typeof(SubsystemPlayers));
                        if (playerData != null) playerList.Remove(playerData);
                    }
                    if (entity != null && entity.IsAddedToProject)
                        project.RemoveEntity(entity, disposeEntity: true);
                    playerData?.Dispose();
                }
                catch (Exception rollbackError)
                {
                    m_diagnostics?.Error(m_ownerId,
                        "回滚 clientId " + clientId + " 的联机角色时又失败：" + rollbackError.GetType().Name +
                        " " + rollbackError.Message, rollbackError);
                }
                failureReason = ex.GetType().Name + " " + ex.Message;
                m_diagnostics?.Error(m_ownerId,
                    "创建 clientId " + clientId + " 的联机角色失败（已回滚，不再重试创建）：" + failureReason, ex);
                return false;
            }
            finally
            {
                m_creating.Remove(clientId);
            }
        }

        /// <summary>
        /// 从活着的 PlayData 抓一份角色记录（生命/背包/位置等**主机权威**字段）。
        /// Source: ScMultiplayerProfileHandlers.cs:376-467（CapturePlayerRecord）
        /// </summary>
        public NetworkPlayerRecord Capture(int clientId)
        {
            if (!m_avatars.TryGetValue(clientId, out PlayerData playerData)) return null;
            return CapturePlayerRecord(playerData);
        }

        /// <summary>
        /// 把该化身的实体从世界里摘掉。
        /// Source: ScMultiplayerWorldObjectHandlers.cs:348-427（RemoveNetworkPlayer 的引擎清理部分，
        /// 不含角色记录落盘/挂起状态 —— 那些属于"断线挂起"那条链）。
        /// ⚠️ **目前没有任何调用方**：断线/离开房间的清理（含断线挂起、自动重连复用同一身份）
        /// 还没移植，所以这里只把引擎动作备好；接线时必须同时接上"离开时抓记录并落盘"。
        /// </summary>
        public bool Remove(int clientId)
        {
            if (!m_avatars.TryGetValue(clientId, out PlayerData playerData)) return false;
            m_avatars.Remove(clientId);
            m_clientRecordKeys.Remove(clientId);
            // 旧实现在 UpdateHostTerrainInterestTable 的"过期客户端"清扫里同样把上报半径删掉。
            m_hostTerrainReportedInterestRadii.Remove(clientId);
            // 角色离开 → 它那条引擎地形更新位置必须摘掉，否则引擎会继续在这个"已经不存在的角色"
            // 周围加载 / 保留区块（主机白加载，实时地图还会多出一片没有人站着的区域）。
            Project project = GameManager.Project;
            RemoveHostTerrainUpdateLocation(project, clientId, playerData);
            // 旧 RemoveNetworkPlayer 同时把该角色的位置状态丢掉（重连后重新对齐，不沿用旧轨迹）。
            ForgetRemotePosition(clientId);
            m_updateLocationDriftSinceMs.Remove(clientId);
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(false);
            try
            {
                if (players != null)
                {
                    m_parentField.GetParentField<List<PlayerData>>(players, "m_playersData",
                        typeof(SubsystemPlayers)).Remove(playerData);
                    List<ComponentPlayer> componentPlayers = m_parentField.GetParentField<List<ComponentPlayer>>(
                        players, "m_componentPlayers", typeof(SubsystemPlayers));
                    if (playerData.ComponentPlayer != null)
                    {
                        componentPlayers.Remove(playerData.ComponentPlayer);
                        if (playerData.ComponentPlayer.Entity != null &&
                            playerData.ComponentPlayer.Entity.IsAddedToProject)
                        {
                            project.RemoveEntity(playerData.ComponentPlayer.Entity, disposeEntity: true);
                        }
                    }
                }
                playerData.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                m_diagnostics?.Error(m_ownerId, "移除 clientId " + clientId + " 的联机角色失败：", ex);
                return false;
            }
        }

        /// <summary>会话结束（换世界 / 下线）：忘掉全部化身的引用，不主动改世界状态。</summary>
        public void ForgetAll()
        {
            m_avatars.Clear();
            m_clientRecordKeys.Clear();
            m_creating.Clear();
            m_hostTerrainReportedInterestRadii.Clear();
            // 不在这里调引擎 RemoveUpdateLocation：换世界时 GameManager.Project 已经是**新世界**，
            // 旧角色的 PlayerIndex 在新世界里可能正好是本机玩家的槽位 —— 盲删会摘掉新世界的本机相机。
            // 角色真正"离开"的路径是 Remove(clientId)（ClientLeft），那里有活着的 Project + 角色上下文。
            m_hostTerrainLocations.Clear();
            m_updateLocationDriftSinceMs.Clear();
            m_reportedOnce.Clear();
            m_remotePositions.Clear();
        }

        // ------------------------------------------------------- 地形兴趣（旧实现 MaintainRemoteTerrainLocations 整块）

        /// <summary>
        /// 主机：记下某个玩家端上报的兴趣半径。
        /// Source: Mod/ScMultiplayer/Modules/Terrain/ScMultiplayerTerrainHandlers.cs:441-449
        ///         （Interest 分支：1..MaximumTerrainInterestRadius 之外一律不收，保留上一次的值）
        /// </summary>
        public void SetReportedInterestRadius(int clientId, int radius)
        {
            if (clientId <= 0)
            {
                ReportProblemOnce("terrain-interest-bad-client:" + clientId,
                    "收到非法 clientId=" + clientId + " 的兴趣半径上报 → 丢弃（不拿它去算地形可见范围）");
                return;
            }
            if (radius < 1 || radius > MaximumTerrainInterestRadius)
            {
                ReportProblemOnce("terrain-interest-bad-radius:" + clientId,
                    "玩家端 clientId=" + clientId + " 上报的兴趣半径 " + radius + " 非法（合法范围 1.." +
                    MaximumTerrainInterestRadius + "）→ 不收，保留上一次的值");
                return;
            }
            m_hostTerrainReportedInterestRadii.TryGetValue(clientId, out int previous);
            m_hostTerrainReportedInterestRadii[clientId] = radius;
            if (previous != radius)
            {
                // 低频（只在变化时）：实机排查"主机有没有按客户端的可见范围给角色挂地形更新位置"靠这一行。
                Log("玩家端 clientId=" + clientId + " 上报兴趣半径 " + radius + " 区块 → 它周围的引擎地形" +
                    "可见范围 = " + (radius * 16f) + "（旧实现 remoteVisibility = radius * 16f）");
            }
        }

        /// <summary>
        /// 引擎的"地形更新位置"维护 —— 旧实现 <c>MaintainRemoteTerrainLocations</c> 的搬运。
        ///
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:460-516
        ///         （MaintainRemoteTerrainLocations；旧实现每帧由 Modules/Runtime/ScMultiplayerUpdateLoop.cs:1303 调用）
        /// Source: Survivalcraft/Game/TerrainUpdater.cs:TerrainUpdater.SetUpdateLocation / RemoveUpdateLocation
        ///
        /// 主机分支（旧 :498-515）：给**每个联机角色**的 PlayerIndex 挂更新位置
        ///   <c>SetUpdateLocation(playerData.PlayerIndex, body.Position.XZ, remoteVisibility, contentDistance)</c>，
        ///   其中 <c>remoteVisibility = radius * 16f</c>、<c>radius</c> = 该玩家端上报的兴趣半径
        ///   （没上报过就用主机可见范围算出的 defaultRadius）—— 引擎的更新线程因此会在客户端角色周围
        ///   生成地形，而不是只围绕主机自己的摄像机。
        ///   **内容距离也从同一份可见范围来**（用户口径："同步范围根据客户端的可见范围设置"）：
        ///   旧实现是硬编码 <c>64f</c>，半径 8 时可见范围 128 而内容距离 64（内容范围比声明的可见范围小）。
        ///   算法见 <see cref="TerrainSyncRange.Distances"/>（夹紧口径也在那里，只有一份）。
        /// 客户端分支（旧 :471-497）：远端化身的更新位置**摘掉**（它们不是本机视角，挂了会把别人的探索
        ///   变成本机的 Apply 积压），只给**本机玩家**挂
        ///   <c>SetUpdateLocation(localPlayer.PlayerIndex, body.Position.XZ, clientVisibility, clientVisibility)</c>，
        ///   其中 <c>clientVisibility = SubsystemSky.VisibilityRange ?? 64f</c>。
        ///
        /// 引擎的 TerrainUpdater 用 <c>Dictionary&lt;int, UpdateLocation&gt;</c> 按 PlayerIndex 索引
        /// （旧注释里的"最多 4 个位置"= SubsystemGameWidgets.MaxGameWidgets 个玩家槽位；本仓库的
        /// TerrainUpdater.cs:230/256 是字典，不存在的槽位不会被分配），所以这里不存在"抢不到槽位"的
        /// 分支；拿不到 Project / TerrainUpdater / 本机玩家时**明确报错并跳过**，不静默挑一个替代目标。
        /// </summary>
        public void ApplyTerrainUpdateLocations(bool isHost)
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                ReportProblemOnce("terrain-locations-no-project",
                    "还没有加载中的 Project（没进世界）→ 本帧不维护地形更新位置");
                return;
            }
            SubsystemTerrain terrain = project.FindSubsystem<SubsystemTerrain>(throwOnError: false);
            if (terrain?.TerrainUpdater == null)
            {
                ReportProblemOnce("terrain-locations-no-updater",
                    "找不到 SubsystemTerrain.TerrainUpdater → 无法维护地形更新位置" +
                    "（联机角色周围不会生成地形）");
                return;
            }
            if (!isHost)
            {
                foreach (PlayerData remotePlayer in m_avatars.Values.ToArray())
                {
                    if (remotePlayer?.PlayerIndex >= 0)
                        terrain.TerrainUpdater.RemoveUpdateLocation(remotePlayer.PlayerIndex);
                }
                // 客户端不给远端化身挂更新位置（上面的 RemoveUpdateLocation 已经摘掉）：
                // 账本同步清空，状态行不会残留"主机侧位置"的假象。
                m_hostTerrainLocations.Clear();
                m_updateLocationDriftSinceMs.Clear();
                if (!TryGetLocalPlayer(project, out PlayerData localPlayer, out string localReason))
                {
                    ReportProblemOnce("terrain-locations-no-local-player",
                        "找不到本机玩家（旧实现的规则：PlayersData 里不是联机角色的那一条）：" +
                        localReason + " → 本机地形不会更新");
                    return;
                }
                if (localPlayer.PlayerIndex < 0)
                {
                    ReportProblemOnce("terrain-locations-local-index",
                        "本机玩家还没有引擎玩家槽位（PlayerIndex=" + localPlayer.PlayerIndex +
                        "）→ 本机地形不会更新");
                    return;
                }
                float clientVisibility = ResolveVisibility(project);
                terrain.TerrainUpdater.SetUpdateLocation(localPlayer.PlayerIndex,
                    localPlayer.ComponentPlayer.ComponentBody.Position.XZ,
                    clientVisibility, clientVisibility);
                return;
            }

            float hostVisibility = ResolveVisibility(project);
            int defaultRadius = GetTerrainInterestRadius(hostVisibility);
            long nowMs = (long)(Time.RealTime * 1000.0);
            // 引擎的更新位置是 Dictionary<int, UpdateLocation>，**只按 PlayerIndex 索引**：
            // 两条位置共用同一个 index 会互相覆盖（后写的那条生效），也就是"只有一个角色的地形在加载"。
            // 实机数据（2026-10-18 18:44）本机玩家 index=1、联机角色 0/2 → 没有碰撞；这里把它变成显式检查，
            // 以后换世界 / 重建角色一旦撞上就会在日志里说明，而不是静默地少加载一片地形。
            TryGetLocalPlayer(project, out PlayerData hostLocalPlayer, out _);
            foreach (KeyValuePair<int, PlayerData> item in m_avatars.ToArray())
            {
                PlayerData playerData = item.Value;
                if (playerData?.ComponentPlayer?.ComponentBody == null)
                {
                    ReportProblemOnce("terrain-locations-avatar-body:" + item.Key,
                        "联机角色 clientId=" + item.Key +
                        " 还没有 ComponentBody → 本帧不给它挂地形更新位置");
                    continue;
                }
                Vector3 position = playerData.ComponentPlayer.ComponentBody.Position;
                if (playerData.PlayerIndex < 0)
                {
                    ReportProblemOnce("terrain-locations-avatar-index:" + item.Key,
                        "联机角色 clientId=" + item.Key + " 没有引擎玩家槽位（PlayerIndex=" +
                        playerData.PlayerIndex + "）→ 本帧不给它挂地形更新位置");
                    // 身体在、槽位没有：更新位置挂不上，按"角色离最后一条更新位置多远"计时告警。
                    NoteUpdateLocationDrift(item.Key, position, nowMs);
                    continue;
                }
                if (hostLocalPlayer != null && hostLocalPlayer.PlayerIndex == playerData.PlayerIndex &&
                    !ReferenceEquals(hostLocalPlayer, playerData))
                {
                    ReportProblemOnce("terrain-locations-index-local:" + item.Key,
                        "联机角色 clientId=" + item.Key + " 的 PlayerIndex=" + playerData.PlayerIndex +
                        " 与本机玩家相同 → 两条地形更新位置会互相覆盖（引擎按 PlayerIndex 索引）；" +
                        "主机可能只在其中一方周围加载地形");
                }
                foreach (KeyValuePair<int, PlayerData> other in m_avatars)
                {
                    if (other.Key == item.Key || other.Value?.PlayerIndex != playerData.PlayerIndex) continue;
                    ReportProblemOnce("terrain-locations-index-duplicate:" + item.Key,
                        "联机角色 clientId=" + item.Key + " 与 clientId=" + other.Key +
                        " 共用 PlayerIndex=" + playerData.PlayerIndex +
                        " → 后写的那条会覆盖前者，主机不会同时在这两个角色周围加载地形");
                    break;
                }
                bool fromReport = m_hostTerrainReportedInterestRadii.TryGetValue(item.Key,
                    out int reportedRadius);
                int radius = fromReport
                    ? Math.Max(1, Math.Min(MaximumTerrainInterestRadius, reportedRadius))
                    : defaultRadius;
                // 可见范围 = 内容距离 = radius * 16f（夹紧在 1..MaximumTerrainInterestRadius 区块），
                // 也就是主机为这个客户端生成 / 保留地形的范围**完全跟随它声明的可见范围**。
                // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:510-512
                //         （旧实现此处是 SetUpdateLocation(..., remoteVisibility, 64f)：内容距离硬编码）
                TerrainSyncRange.Distances(radius, out float remoteVisibility, out float contentDistance);
                bool hadLocation = m_hostTerrainLocations.TryGet(item.Key,
                    out HostTerrainLocation previousLocation);
                if (hadLocation && previousLocation.PlayerIndex != playerData.PlayerIndex)
                {
                    // 槽位变了（重建 / 复用）：先摘旧槽位，否则引擎里会留下一条永远不动的位置。
                    terrain.TerrainUpdater.RemoveUpdateLocation(previousLocation.PlayerIndex);
                    LogRateLimited("terrain-locations-index:" + item.Key,
                        "联机角色 clientId=" + item.Key + " 的引擎玩家槽位从 PlayerIndex=" +
                        previousLocation.PlayerIndex + " 变成 " + playerData.PlayerIndex +
                        " → 先摘掉旧槽位的更新位置，再在新区块挂新的（不留第二条）",
                        UpdateLocationLogIntervalMs);
                }
                // 账本先记：状态行 / 偏离告警 / 自检都读这一份，和交给引擎的是同一组值（位置 + 半径 + 内容距离）。
                bool changed = m_hostTerrainLocations.Apply(item.Key, playerData.PlayerIndex,
                    position.XZ, radius, contentDistance);
                terrain.TerrainUpdater.SetUpdateLocation(playerData.PlayerIndex, position.XZ,
                    remoteVisibility, contentDistance);
                if (!hadLocation)
                {
                    // 每个（clientId, 半径）只写一行：实机确认"主机确实在客户端角色周围生成地形"用这一行。
                    // 实际生效的两个距离（可见范围 / 内容距离）都写出来；区块也写出来，和客户端当前区块直接对数。
                    LogOnce("terrain-locations-applied:" + item.Key + ":" + radius,
                        "已给联机角色 clientId=" + item.Key + " 挂引擎地形更新位置：PlayerIndex=" +
                        playerData.PlayerIndex + " · 兴趣半径 " + radius + " 区块（" +
                        (fromReport ? "客户端上报" : "主机默认：客户端尚未上报") + "）· 可见范围 " +
                        remoteVisibility + "（= radius * 16f）· 内容距离 " + contentDistance +
                        "（跟随可见范围；旧实现硬编码 64）· 区块 " +
                        HostTerrainLocationTable.ChunkOf(position.XZ));
                }
                else if (changed)
                {
                    // 移动日志限流（每 clientId 最多 5 秒一行）：实时地图要验证"地形填充跟着角色走"看这一行。
                    LogRateLimited("terrain-locations-move:" + item.Key,
                        "联机角色 clientId=" + item.Key + " 的地形更新位置跟随到区块 " +
                        HostTerrainLocationTable.ChunkOf(position.XZ) + "（PlayerIndex=" +
                        playerData.PlayerIndex + " · 兴趣半径 " + radius + " · 内容距离 " +
                        contentDistance + " · 累计移动 " + m_hostTerrainLocations.Moves +
                        " 次；角色 ComponentBody 与更新位置同点，不偏离）",
                        UpdateLocationLogIntervalMs);
                }
                // 正常路径每 tick 都把位置刷成角色的 ComponentBody.Position → 偏离立刻归零。
                m_updateLocationDriftSinceMs.Remove(item.Key);
            }
            // 旧实现在这里还会调 UpdateHostTerrainInterestTable(project) —— 那张"区块订阅表"在本重写版里
            // 由 TerrainSyncHost（Terrain/TerrainSync.cs:RefreshWindow + m_subscribers）按客户端上报的
            // InterestUpdate 自己维护，这里不再造第二份真相。
        }

        // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:521-525（GetTerrainInterestRadius）
        // 口径与 TerrainSyncRange.RadiusForVisibility 完全一致（唯一实现）：max(1, ceil(visibility/16))，
        // 夹到 1..MaximumTerrainInterestRadius。客户端上报与主机默认半径因此不会漂移。
        public int GetTerrainInterestRadius(float visibility)
        {
            int radius = TerrainSyncRange.RadiusForVisibility(visibility);
            return Math.Max(1, Math.Min(MaximumTerrainInterestRadius, radius));
        }

        /// <summary>
        /// 旧实现的"本机玩家"规则：<c>PlayersData</c> 里**不是网络化身**的那一条。
        /// Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:481-484
        ///         （localPlayers.PlayersData.FirstOrDefault(item => !m_networkPlayerData.Values.Contains(item))）
        /// 拿不到就说明原因（不猜、不退回"第一个有 GameWidget 的角色"）。
        /// </summary>
        public bool TryGetLocalPlayer(Project project, out PlayerData localPlayer, out string reason)
        {
            localPlayer = null;
            reason = null;
            SubsystemPlayers players = project?.FindSubsystem<SubsystemPlayers>(throwOnError: false);
            if (players == null)
            {
                reason = "世界里找不到 SubsystemPlayers";
                return false;
            }
            PlayerData candidate = null;
            foreach (PlayerData item in players.PlayersData)
            {
                if (Contains(item)) continue;
                candidate = item;
                break;
            }
            if (candidate == null)
            {
                reason = "PlayersData 里除了 " + m_avatars.Count + " 个联机角色以外没有任何角色";
                return false;
            }
            if (candidate.ComponentPlayer?.ComponentBody == null)
            {
                reason = "本机玩家（PlayersData 里不是联机角色的那一条）还没有 ComponentPlayer/ComponentBody";
                return false;
            }
            localPlayer = candidate;
            return true;
        }

        // Source: Mod/ScMultiplayer/Modules/World/ScMultiplayerWorldObjectHandlers.cs:486 / :498
        //         （clientVisibility / hostVisibility = project.FindSubsystem<SubsystemSky>(false)?.VisibilityRange ?? 64f）
        private float ResolveVisibility(Project project)
        {
            SubsystemSky sky = project.FindSubsystem<SubsystemSky>(throwOnError: false);
            if (sky == null)
            {
                // 旧实现同样退到 64f；这里把原因写出来（不静默）
                ReportProblemOnce("terrain-locations-no-sky",
                    "找不到 SubsystemSky → 可见范围按旧实现的兜底值 64 使用");
                return 64f;
            }
            return sky.VisibilityRange;
        }

        private void ReportProblemOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            m_diagnostics?.Warn(m_ownerId, message);
        }

        private void LogOnce(string key, string message)
        {
            if (!m_reportedOnce.Add(key)) return;
            Log(message);
        }

        /// <summary>
        /// 限流日志（同一个 key 至少间隔 intervalMs 才写一行）：位置每 tick 都在变，
        /// 不设限会刷屏；但**绝不静默** —— 计数（updateLocationMoves）照常累加。
        /// </summary>
        private void LogRateLimited(string key, string message, long intervalMs)
        {
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (m_lastRateLimitedLogMs.TryGetValue(key, out long last) &&
                nowMs - last < intervalMs) return;
            m_lastRateLimitedLogMs[key] = nowMs;
            Log(message);
        }

        /// <summary>
        /// 联机角色"离它最后一条地形更新位置 &gt; 8 区块"持续超过 2 秒 → 限流告警。
        /// 正常路径每次 <see cref="ApplyTerrainUpdateLocations"/> 都会把位置刷到角色的
        /// <c>ComponentBody.Position</c>（偏离 0）；只有更新位置挂不上 / 没刷新时这里才会累积，
        /// 那正是"主机不在角色周围加载地形"的征兆。
        /// </summary>
        private void NoteUpdateLocationDrift(int clientId, Vector3 position, long nowMs)
        {
            if (!m_hostTerrainLocations.TryGet(clientId, out HostTerrainLocation location)) return;
            ChunkKey bodyChunk = HostTerrainLocationTable.ChunkOf(position.XZ);
            int distance = HostTerrainLocationTable.ChunkDistance(bodyChunk, location.Chunk);
            if (distance <= UpdateLocationDriftChunkThreshold)
            {
                m_updateLocationDriftSinceMs.Remove(clientId);
                return;
            }
            if (!m_updateLocationDriftSinceMs.TryGetValue(clientId, out long since))
            {
                m_updateLocationDriftSinceMs[clientId] = nowMs;
                return;
            }
            if (nowMs - since < UpdateLocationDriftWarningMs) return;
            LogRateLimited("terrain-locations-drift:" + clientId,
                "联机角色 clientId=" + clientId + " 的 ComponentBody 在区块 " + bodyChunk +
                "，而它的引擎地形更新位置还停在 " + location.Chunk + "（相距 " + distance +
                " 区块 > " + UpdateLocationDriftChunkThreshold + "）已超过 " +
                (nowMs - since) / 1000 + " 秒 → 主机不会在它周围加载地形" +
                "（检查这个角色有没有拿到 PlayerIndex / ComponentBody；正常路径每 tick 都会刷新到同一点）",
                UpdateLocationLogIntervalMs);
        }

        /// <summary>
        /// 角色离开时摘掉它在引擎里的地形更新位置。账本里的 PlayerIndex 是**挂的时候实际用的那个**
        /// （比 PlayerData.PlayerIndex 更可靠：角色被 Dispose 后 PlayerData 可能已经变了）。
        /// 世界正在卸载（没有 TerrainUpdater）时只记一行事实：引擎会随世界一起丢掉这条位置。
        /// </summary>
        private void RemoveHostTerrainUpdateLocation(Project project, int clientId,
            PlayerData playerData)
        {
            bool hadLocation = m_hostTerrainLocations.TryGet(clientId,
                out HostTerrainLocation location);
            m_hostTerrainLocations.Remove(clientId);
            if (!hadLocation) return;
            int playerIndex = location.PlayerIndex >= 0
                ? location.PlayerIndex
                : (playerData?.PlayerIndex ?? -1);
            if (playerIndex < 0) return;
            TerrainUpdater updater =
                project?.FindSubsystem<SubsystemTerrain>(throwOnError: false)?.TerrainUpdater;
            if (updater == null)
            {
                ReportProblemOnce("terrain-locations-remove-no-updater:" + clientId,
                    "联机角色 clientId=" + clientId + " 离开时找不到 TerrainUpdater（世界正在卸载）→ " +
                    "它的引擎地形更新位置（PlayerIndex=" + playerIndex +
                    "）没有显式摘除；引擎会随这个世界一起丢掉它");
                return;
            }
            updater.RemoveUpdateLocation(playerIndex);
            Log("联机角色 clientId=" + clientId + " 离开 → 已摘掉它的引擎地形更新位置（PlayerIndex=" +
                playerIndex + " · 区块 " + location.Chunk + "）；主机不再为它加载地形");
        }

        // ================================================================ 玩家位置同步
        //
        // 旧实现的完整链路（本重写版把它的**位置/姿态**段整体搬过来）：
        //   · 客户端 → 主机：旧 ScMultiplayerPlayerSync.cs:38-101 的 !IsHost 分支
        //     （SendGamePlayerPositionMessage → SendGamePlayerInputMessage，位置/姿态/速度同一份快照）；
        //   · 主机 → 客户端：同一个方法的主机分支（本机玩家 + 每个联机角色折成 positionBatch →
        //     NetworkMessageSender.SendPlayerPositionBatch 广播）；
        //   · 接收侧写只读状态：ScMultiplayerMessageHandlers.cs:38-97（HandleGamePlayerPositionMessage）；
        //   · 应用到身体（插值/平滑）：ScMultiplayerClientEvents.cs:1054-1160
        //     （UpdateRemotePlayerPresentations，两端每帧各调一次）。

        /// <summary>clientId → 该联机角色最近一份权威位置 + 插值状态（旧 RemotePlayers / NetworkPlayerState）。</summary>
        private readonly Dictionary<int, RemotePlayerPositionState> m_remotePositions =
            new Dictionary<int, RemotePlayerPositionState>();

        public int RemotePositionCount => m_remotePositions.Count;

        /// <summary>已应用到引擎身体的位置快照数（实机排查"主机有没有收到客户端位置"看这个）。</summary>
        public long RemotePositionsApplied { get; private set; }

        /// <summary>因为化身/身体缺失而跳过的位置快照数（跳过时都写了明确原因）。</summary>
        public long RemotePositionsSkipped { get; private set; }

        /// <summary>
        /// 客户端：本机玩家的一份位置/姿态快照。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:53-92
        ///         （本机玩家 = PlayersData 里不是网络化身的那一条；位置/旋转/速度/视线角同一份）
        /// 拿不到就返回 false 并写清原因 —— **绝不猜一个位置**（旧实现同样直接 return）。
        /// </summary>
        public bool TryCaptureLocalPlayerPosition(int serverTick, out PlayerPositionSnapshot snapshot,
            out string reason)
        {
            snapshot = default;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return false;
            }
            if (!TryGetLocalPlayer(project, out PlayerData localPlayer, out reason)) return false;
            return TryCapturePlayerPosition(localPlayer, serverTick, out snapshot, out reason);
        }

        /// <summary>
        /// 主机：把"本机玩家 + 每个联机角色"折成一批位置快照。
        /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:47-100
        ///         （本机玩家用 <c>client.ClientID</c> 当编号 —— 主机是 0；每个联机角色用自己的 clientId；
        ///           ComponentBody 还没好的角色跳过而不是拿别的位置顶）
        /// </summary>
        public int CapturePlayerPositionBatch(int serverTick, List<PlayerPositionEntry> target,
            out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "调用方没有给批次容器";
                return 0;
            }
            target.Clear();
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进世界）";
                return 0;
            }
            int count = 0;
            // 主机自己那一条：编号 = 本机通道槽位（主机 = 0，与客户端建"主机角色"用的槽位键一致）。
            if (!TryGetLocalPlayer(project, out PlayerData localPlayer, out string localReason))
            {
                ReportProblemOnce("position-batch-local",
                    "主机自己的位置快照取不到（" + localReason + "）→ 本拍不带上自己的位置" +
                    "（客户端上的主机角色保持上一份权威位置，而不是被拉到一个猜测点）");
            }
            else if (!TryCapturePlayerPosition(localPlayer, serverTick,
                out PlayerPositionSnapshot localSnapshot, out localReason))
            {
                ReportProblemOnce("position-batch-local",
                    "主机自己的位置快照取不到（" + localReason + "）→ 本拍不带上自己的位置" +
                    "（客户端上的主机角色保持上一份权威位置，而不是被拉到一个猜测点）");
            }
            else
            {
                target.Add(new PlayerPositionEntry(PlayerAvatarModule.HostAvatarSlotKey,
                    in localSnapshot));
                count++;
            }
            foreach (KeyValuePair<int, PlayerData> item in m_avatars.ToArray())
            {
                PlayerData playerData = item.Value;
                if (!TryCapturePlayerPosition(playerData, serverTick, out PlayerPositionSnapshot snapshot,
                    out string avatarReason))
                {
                    ReportProblemOnce("position-batch-avatar:" + item.Key,
                        "联机角色 clientId=" + item.Key + " 的位置快照取不到（" + avatarReason +
                        "）→ 本拍跳过它（不拿别的位置顶替）");
                    continue;
                }
                target.Add(new PlayerPositionEntry(item.Key, in snapshot));
                count++;
            }
            return count;
        }

        // Source: Mod/ScMultiplayer/Modules/Player/PlayerReadOnlyStateCapture.cs:12-32（TryCapture）
        //         + ScMultiplayerSupportTypes.cs:118-156（NetworkPlayerState 的位置字段）
        private bool TryCapturePlayerPosition(PlayerData playerData, int serverTick,
            out PlayerPositionSnapshot snapshot, out string reason)
        {
            snapshot = default;
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentBody body = player?.ComponentBody;
            ComponentLocomotion locomotion = player?.ComponentLocomotion;
            if (body == null)
            {
                reason = "没有 ComponentPlayer/ComponentBody";
                return false;
            }
            if (locomotion == null)
            {
                // 旧 TryCapture 在 locomotion == null 时同样直接失败（视线角与飞行状态都取不到）。
                reason = "没有 ComponentLocomotion（视线角与创造飞行状态取不到）";
                return false;
            }
            // Source: PlayerReadOnlyStateCapture.cs:20-30 —— 逐条照搬旧实现的判据：
            //   着地 = StandingOnValue 有值 且 竖直速度 < 0.1；
            //   蹲下 = TargetCrouchFactor > 0；飞行 = locomotion.IsCreativeFlyEnabled。
            bool isGrounded = body.StandingOnValue.HasValue &&
                MathUtils.Abs(body.Velocity.Y) < 0.1f;
            snapshot = new PlayerPositionSnapshot(serverTick, body.Position, body.Rotation, body.Velocity,
                locomotion.LookAngles, body.TargetCrouchFactor > 0f, locomotion.IsCreativeFlyEnabled,
                isGrounded);
            // Source: PlayerReadOnlyStateCapture.cs:31（TryCapture 返回 snapshot.IsFinite）
            if (!snapshot.IsFinite)
            {
                reason = "位置/旋转/速度/视线角含 NaN/Inf（引擎状态异常）";
                snapshot = default;
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// 收到一份远端角色的位置快照：校验 → 丢弃陈旧 → 记进插值状态 → 立刻应用的姿态字段。
        /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:38-97
        ///         （HandleGamePlayerPositionMessage：IsFinite 校验、ServerTick 陈旧丢弃、
        ///           readOnlyState.ApplyTo(state)、body.TargetCrouchFactor、locomotion.m_lookAngles、
        ///           locomotion.IsCreativeFlyEnabled、LastUpdateTime = Time.RealTime）
        /// 化身 / ComponentBody 缺失 → **明确写原因并跳过**（限流一行），绝不伪造一个位置。
        /// </summary>
        public void ApplyRemotePlayerPosition(int clientId, in PlayerPositionSnapshot snapshot)
        {
            if (!m_avatars.TryGetValue(clientId, out PlayerData playerData) ||
                playerData?.ComponentPlayer?.ComponentBody == null)
            {
                RemotePositionsSkipped++;
                ReportProblemOnce("position-no-avatar:" + clientId,
                    "收到 clientId=" + clientId + " 的位置快照，但本机没有它的联机角色（或还没建出 " +
                    "ComponentBody）→ 跳过这次更新（绝不拿别的角色/默认点顶替；" +
                    "联机角色建好后位置会立刻跟上）");
                return;
            }
            if (!m_remotePositions.TryGetValue(clientId, out RemotePlayerPositionState state))
            {
                state = new RemotePlayerPositionState { ClientId = clientId };
                m_remotePositions[clientId] = state;
            }
            // 旧 :62-65：迟到的旧快照直接丢弃（TCP 有序，这里是防御性判定，照旧保留）。
            if (state.ServerTick >= 0 && snapshot.ServerTick < state.ServerTick) return;
            double now = Time.RealTime;
            float arrivalAge = state.LastUpdateTime <= 0.0 ? 0f : (float)(now - state.LastUpdateTime);
            state.Apply(in snapshot, arrivalAge, now);
            ApplyRemotePlayerPose(playerData, state);
            RemotePositionsApplied++;
        }

        // Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:129-153
        //         （Handler 里"立刻"写进引擎的姿态字段：蹲下目标、视线角、创造飞行开关）
        private void ApplyRemotePlayerPose(PlayerData playerData, RemotePlayerPositionState state)
        {
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentBody body = player?.ComponentBody;
            if (body == null) return;
            body.TargetCrouchFactor = state.IsCrouching ? 1f : 0f;
            ComponentLocomotion locomotion = player.ComponentLocomotion;
            if (locomotion == null) return;
            // 旧 :131-137：机身旋转带 yaw，俯仰单独存在 m_lookAngles 里 —— 两者都要同步。
            m_parentField.ModifyParentField(locomotion, "m_lookAngles", state.LookAngles,
                typeof(ComponentLocomotion));
            locomotion.IsCreativeFlyEnabled = state.IsFlying;
        }

        /// <summary>
        /// 每帧把权威位置应用到远端角色的身体上（插值/平滑）。
        ///
        /// Source: Mod/ScMultiplayer/Modules/Session/ScMultiplayerClientEvents.cs:1054-1160
        ///         （UpdateRemotePlayerPresentations —— 逐行搬运：外推时间、首帧/大误差直接对齐、
        ///           飞行 Lerp、死区 + 追赶速度 + Lerp/Slerp）。
        /// 与旧实现的两处**受控差异**（都在报告里写明）：
        ///   ① <c>state.EstimatedDelay</c> 由接收侧用相邻快照到达间隔估（旧实现用共享网络步差），
        ///      见 <see cref="RemotePlayerPositionState.EstimatedDelay"/>；
        ///   ② 旧 :1086-1094 的骑乘跳过分支与 :1127 的击退死区分支所依赖的链（骑乘 / 受击击退）
        ///      尚未移植，默认值下与原判定等价（死区 = 着地 0.2 / 空中 0.1）。
        /// </summary>
        public void UpdateRemotePresentations(float deltaTime)
        {
            if (m_remotePositions.Count == 0) return;
            float dt = MathUtils.Clamp(deltaTime, 0f, 0.1f);
            double now = Time.RealTime;
            foreach (KeyValuePair<int, RemotePlayerPositionState> item in m_remotePositions.ToArray())
            {
                RemotePlayerPositionState state = item.Value;
                // 旧 :1061：快照过期（> 2 秒 = 对方停了/掉线）或没有化身/身体 → 本帧不动它
                if (state == null ||
                    now - state.LastUpdateTime > RemotePlayerPositionState.RemotePresentationStaleTime)
                    continue;
                if (!m_avatars.TryGetValue(item.Key, out PlayerData playerData) ||
                    playerData?.ComponentPlayer?.ComponentBody == null)
                    continue;
                ComponentBody body = playerData.ComponentPlayer.ComponentBody;
                ComponentLocomotion locomotion = playerData.ComponentPlayer.ComponentLocomotion;
                // 旧 :1068-1076
                body.IsGravityEnabled = !state.IsFlying;
                body.IsGroundDragEnabled = !state.IsFlying;
                if (locomotion != null)
                {
                    locomotion.IsCreativeFlyEnabled = state.IsFlying;
                    m_parentField.ModifyParentField(locomotion, "m_lookAngles", state.LookAngles,
                        typeof(ComponentLocomotion));
                }
                float extrapolationTime = MathUtils.Min(state.EstimatedDelay,
                    RemotePlayerPositionState.RemoteExtrapolationLimit);
                Vector3 targetPosition = state.Position;
                if (!state.IsFlying)
                {
                    targetPosition.X += state.Velocity.X * extrapolationTime;
                    targetPosition.Z += state.Velocity.Z * extrapolationTime;
                }
                if (!state.IsGrounded && !state.IsFlying)
                {
                    targetPosition.Y += state.Velocity.Y * extrapolationTime -
                        4.9f * extrapolationTime * extrapolationTime;
                }
                float errorSquared = Vector3.DistanceSquared(body.Position, targetPosition);
                if (!state.PresentationInitialized || errorSquared > 9f)
                {
                    // 旧 :1108-1116：第一次 / 误差超过 3 格 → 直接对齐（旋转与速度一起），不平滑。
                    body.Position = state.Position;
                    body.Rotation = state.Rotation;
                    body.Velocity = state.Velocity;
                    state.PresentationInitialized = true;
                    continue;
                }
                if (state.IsFlying)
                {
                    // 旧 :1117-1124：飞行时用与帧率无关的指数混合直接跟位置，速度清零。
                    float positionBlend = 1f - MathUtils.Pow(0.001f, MathUtils.Min(dt, 0.1f));
                    body.Position = Vector3.Lerp(body.Position, state.Position, positionBlend);
                    body.Velocity = Vector3.Zero;
                    body.Rotation = Quaternion.Slerp(body.Rotation, state.Rotation, positionBlend);
                    continue;
                }
                float delayFactor = MathUtils.Saturate(state.EstimatedDelay / 0.2f);
                Vector3 error = targetPosition - body.Position;
                float deadZone = state.IsGrounded ? 0.2f : 0.1f;
                Vector3 targetVelocity = state.Velocity;
                if (state.IsGrounded) targetVelocity.Y = 0f;
                Vector3 desiredVelocity;
                float blend;
                if (error.LengthSquared() <= deadZone * deadZone)
                {
                    // 旧 :1135-1139：死区内只跟速度，不追位置（避免抖动）
                    desiredVelocity = targetVelocity;
                    blend = 0.45f;
                }
                else
                {
                    // 旧 :1140-1152：有限追赶速度（时延越大允许追得越快），绝不瞬移。
                    float horizon = MathUtils.Lerp(0.35f, 0.2f, delayFactor);
                    Vector3 catchUpVelocity = error / horizon;
                    float maxExtraSpeed = MathUtils.Lerp(3f, 8f, delayFactor);
                    float extraSpeed = catchUpVelocity.Length();
                    if (extraSpeed > maxExtraSpeed) catchUpVelocity *= maxExtraSpeed / extraSpeed;
                    desiredVelocity = targetVelocity + catchUpVelocity;
                    blend = MathUtils.Lerp(0.2f, 0.35f, delayFactor);
                }
                if (!state.IsGrounded) blend = MathUtils.Max(blend, 0.45f);
                body.Velocity = Vector3.Lerp(body.Velocity, desiredVelocity, blend);
                body.Rotation = Quaternion.Slerp(body.Rotation, state.Rotation, 0.4f);
            }
        }

        /// <summary>丢掉某个联机角色的位置状态（旧 RemoveNetworkPlayer 里的 RemotePlayers.Remove）。</summary>
        private void ForgetRemotePosition(int clientId) => m_remotePositions.Remove(clientId);

        // ---------------------------------------------------------------- 引擎动作（逐个对照旧实现）

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.AddPlayerData
        // Source: ScMultiplayerPlayerHealthAndIngress.cs:1033-1041（FindAvailableNetworkPlayerIndex）
        private int FindAvailablePlayerIndex(SubsystemPlayers players)
        {
            var used = new HashSet<int>(players.PlayersData.Select(item => item.PlayerIndex));
            used.UnionWith(m_avatars.Values.Select(item => item.PlayerIndex));
            int playerIndex = 0;
            while (used.Contains(playerIndex)) playerIndex++;
            return playerIndex;
        }

        // Source: Survivalcraft/Game/SubsystemPlayers.cs:SubsystemPlayers.Save
        // 引擎用 m_nextPlayerIndex 发号并把它写进 Project.xml。网络化身与客户端的本机玩家
        // 替换都会临时压低这个计数器；如果它停在某个已有玩家索引上或更低，下一个实体就会拿到
        // 重复索引并绑到已经拥有 ComponentPlayer 的 PlayerData（PlayerData.OnEntityAdded 抛
        // "More than 1 player with PlayerIndex N added to world."）。这里只向上修复：
        // 健康世界永不被改动，也不会丢掉世界的 NextPlayerIndex。
        // Source: ScMultiplayerPlayerHealthAndIngress.cs:1043-1068（RepairEnginePlayerIndexCounter）
        private void RepairEnginePlayerIndexCounter(SubsystemPlayers players)
        {
            if (players == null) return;
            int maxPlayerIndex = -1;
            foreach (PlayerData playerData in players.PlayersData)
            {
                if (playerData != null && playerData.PlayerIndex > maxPlayerIndex)
                    maxPlayerIndex = playerData.PlayerIndex;
            }
            int nextPlayerIndex = m_parentField.GetParentField<int>(
                players, "m_nextPlayerIndex", typeof(SubsystemPlayers));
            if (nextPlayerIndex <= maxPlayerIndex)
            {
                m_parentField.ModifyParentField(players, "m_nextPlayerIndex", maxPlayerIndex + 1,
                    typeof(SubsystemPlayers));
            }
        }

        // 纯协议性别 ↔ 引擎 PlayerClass（数值一一对应；只在这一处转换）。
        // Source: Survivalcraft/Game/PlayerClass.cs
        public static PlayerClass ToPlayerClass(PlayerSex sex) =>
            sex == PlayerSex.Female ? PlayerClass.Female : PlayerClass.Male;

        public static PlayerSex ToPlayerSex(PlayerClass playerClass) =>
            playerClass == PlayerClass.Female ? PlayerSex.Female : PlayerSex.Male;

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        // Source: ScMultiplayerProfileHandlers.cs:92-106（InvokeClientPlaceholderPlayerSpawn）
        private void InvokeClientPlaceholderPlayerSpawn(PlayerData playerData, Vector3 position)
        {
            Type spawnModeType = typeof(PlayerData).GetNestedType("SpawnMode", BindingFlags.NonPublic);
            if (spawnModeType == null)
                throw new MissingMemberException(typeof(PlayerData).FullName, "SpawnMode");
            object respawn = Enum.Parse(spawnModeType, "Respawn");
            playerData.SpawnPosition = position;
            // Respawn 只建本机角色实体；初始物品 / 衣服 / 船等权威状态在联机记录里，
            // 由主机侧记录同步链负责（本重写版尚未下发整份记录 —— 明确见报告，不伪造）。
            m_parentMethod.InvokeParentMethod(playerData, "SpawnPlayer",
                new[] { typeof(Vector3), spawnModeType }, position, respawn);
        }

        /// <summary>
        /// 客户端：把下载世界带来的本机角色替换成资料里的角色（性别不同时唯一正确的做法）。
        /// 保留同一个 PlayerIndex / 输入设备 / 当前位置；新角色立即以 Respawn 出生，
        /// 之后由每帧的远端插值与主机权威位置把它带到正确位置。
        /// Source: ScMultiplayerProfileHandlers.cs:1407-1469（ReplaceLocalPlayerData）
        /// </summary>
        private bool ReplaceLocalPlayerData(Project project, PlayerData current,
            PlayerProfileSnapshot profile, out PlayerData replacement, out string reason)
        {
            replacement = null;
            reason = null;
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
            if (players == null)
            {
                reason = "世界里找不到 SubsystemPlayers";
                return false;
            }
            try
            {
                int playerIndex = current.PlayerIndex;
                WidgetInputDevice inputDevice = current.InputDevice;
                Vector3 position = current.ComponentPlayer?.ComponentBody?.Position ??
                    current.SpawnPosition;
                float level = current.Level;
                players.RemovePlayerData(current);
                replacement = new PlayerData(project)
                {
                    Name = profile.Name,
                    PlayerClass = ToPlayerClass(profile.Appearance?.Sex ?? PlayerSex.Male),
                    CharacterSkinName = profile.Appearance?.SkinName ?? string.Empty,
                    Level = level,
                    InputDevice = inputDevice,
                    SpawnPosition = position != Vector3.Zero
                        ? position
                        : (players.GlobalSpawnPosition != Vector3.Zero
                            ? players.GlobalSpawnPosition
                            : Vector3.Zero)
                };
                // 引擎发号：AddPlayerData 用 m_nextPlayerIndex++；先把计数器压到原槽位再取号。
                m_parentField.ModifyParentField(players, "m_nextPlayerIndex", playerIndex,
                    typeof(SubsystemPlayers));
                players.AddPlayerData(replacement);
                RepairEnginePlayerIndexCounter(players);
                InvokeClientPlaceholderPlayerSpawn(replacement, replacement.SpawnPosition);
                m_parentField.GetParentField<StateMachine>(replacement, "m_stateMachine",
                    typeof(PlayerData)).TransitionTo("Playing");
                return true;
            }
            catch (Exception ex)
            {
                reason = "替换本机角色失败：" + ex.GetType().Name + " " + ex.Message;
                m_diagnostics?.Error(m_ownerId, reason, ex);
                return false;
            }
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        // Source: ScMultiplayerProfileHandlers.cs:69-89（InvokeInitialPlayerSpawn）
        private void InvokeInitialPlayerSpawn(PlayerData playerData, Vector3 position)
        {
            Type spawnModeType = typeof(PlayerData).GetNestedType("SpawnMode", BindingFlags.NonPublic);
            if (spawnModeType == null)
                throw new MissingMemberException(typeof(PlayerData).FullName, "SpawnMode");
            object initialNoIntro = Enum.Parse(spawnModeType, "InitialNoIntro");
            // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.FindNoIntroSpawnPosition
            // 先按"引擎玩家槽位"把临时角色摊在请求位置周围，再做引擎自己的碰撞/地形搜索，
            // 并把结果留作重生锚点。
            float angle = 2f * MathUtils.PI * ((playerData.PlayerIndex - 1) % 3) / 3f;
            Vector3 desiredPosition = position + 3f * new Vector3(
                MathUtils.Cos(angle), 0f, MathUtils.Sin(angle));
            Vector3 spawnPosition = m_parentMethod.InvokeParentMethod<Vector3>(playerData,
                "FindNoIntroSpawnPosition", new[] { typeof(Vector3), typeof(bool) }, desiredPosition, false);
            playerData.SpawnPosition = spawnPosition;
            m_parentMethod.InvokeParentMethod(playerData, "SpawnPlayer",
                new[] { typeof(Vector3), spawnModeType }, spawnPosition, initialNoIntro);
        }

        // Source: Survivalcraft/Game/ShortInventoryWidget.cs:ShortInventoryWidget.MeasureOverride
        // Source: Survivalcraft/Game/ComponentInventory.cs:ComponentInventory.GetSlotCapacity
        // Source: ScMultiplayerProfileHandlers.cs:1627-1633
        private static void ConfigureNetworkPlayerInventory(IInventory inventory)
        {
            // 网络化身没有 ShortInventoryWidget，普通背包否则会保留模板默认的 10，
            // 于是把保留槽 7-9 当成可用槽。
            if (inventory is ComponentInventory && inventory.VisibleSlotsCount != 7)
                inventory.VisibleSlotsCount = 7;
        }

        // Source: ScMultiplayerProfileHandlers.cs:576-618（RestorePlayerRecordInventory）
        private static void RestorePlayerRecordInventory(IInventory inventory, NetworkPlayerRecord record)
        {
            if (inventory == null || record == null) return;
            if (inventory is ComponentCreativeInventory creativeInventory)
            {
                int creativeSlotsCount = Math.Min(creativeInventory.OpenSlotsCount,
                    Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0));
                for (int i = 0; i < creativeSlotsCount; i++)
                {
                    creativeInventory.RemoveSlotItems(i, int.MaxValue);
                    if (record.SlotValues[i] != 0 && record.SlotCounts[i] > 0)
                        creativeInventory.AddSlotItems(i, record.SlotValues[i], 1);
                }
                creativeInventory.CategoryIndex = Math.Max(record.CreativeCategoryIndex, 0);
                creativeInventory.PageIndex = Math.Max(record.CreativePageIndex, 0);
                creativeInventory.ActiveSlotIndex = record.ActiveSlotIndex;
                return;
            }
            if (record.InventoryWasCreative || PlayerRecordCodec.LooksLikeLegacyCreativeInventory(record)) return;
            int slotsCount = Math.Min(inventory.SlotsCount,
                Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0));
            for (int i = 0; i < slotsCount; i++)
            {
                int value = record.SlotValues[i];
                int count = record.SlotCounts[i];
                inventory.RemoveSlotItems(i, int.MaxValue);
                if (value == 0 || count <= 0) continue;
                int capacity;
                try
                {
                    capacity = inventory.GetSlotCapacity(i, value);
                }
                catch
                {
                    continue;
                }
                count = Math.Min(count, capacity);
                if (count > 0) inventory.AddSlotItems(i, value, count);
            }
            inventory.ActiveSlotIndex = record.ActiveSlotIndex;
        }

        // Source: Survivalcraft/Game/FullInventoryWidget.cs:FullInventoryWidget
        // Source: ScMultiplayerProfileHandlers.cs:622-630（RestorePlayerRecordCrafting）
        private void RestorePlayerRecordCrafting(ComponentCraftingTable craftingTable,
            NetworkPlayerRecord record)
        {
            // 结果槽是推导出来的，因此刻意不入角色记录。
            if (craftingTable == null || record == null ||
                record.HandcraftSlotValues == null || record.HandcraftSlotCounts == null)
                return;
            ApplyInventory(craftingTable, record.HandcraftSlotValues, record.HandcraftSlotCounts);
        }

        // Source: ScMultiplayerProfileHandlers.cs:635-691（ApplyPlayerRecordState）
        private void ApplyPlayerRecordState(ComponentPlayer player, NetworkPlayerRecord record)
        {
            if (player == null || record == null) return;
            if (player.ComponentBody != null) player.ComponentBody.Rotation = record.BodyRotation;
            if (player.ComponentLocomotion != null)
            {
                m_parentField.ModifyParentField(player.ComponentLocomotion, "m_lookAngles",
                    record.LookAngles, typeof(ComponentLocomotion));
            }
            bool creativeInventory = player.ComponentMiner?.Inventory is ComponentCreativeInventory;
            if (player.ComponentLocomotion != null)
                player.ComponentLocomotion.IsCreativeFlyEnabled = creativeInventory && record.IsCreativeFlying;
            ComponentVitalStats vitalStats = player.ComponentVitalStats;
            if (vitalStats != null)
            {
                float targetTemperature = MathUtils.Clamp(record.TargetTemperature, 0f, 24f);
                m_parentField.ModifyParentField(vitalStats, "m_targetTemperature", targetTemperature,
                    typeof(ComponentVitalStats));
                // 旧实现这里还调用 SuComponentVitalStats.ApplyAuthoritativeTargetTemperature
                // （客户端权威体温的 Su 组件扩展）。本重写版还没有那个组件 → 不假装调用，见报告"未移植"。
                Dictionary<int, float> satiation = m_parentField.GetParentField<Dictionary<int, float>>(
                    vitalStats, "m_satiation", typeof(ComponentVitalStats));
                if (satiation != null)
                {
                    satiation.Clear();
                    foreach (KeyValuePair<int, float> item in record.Satiation ?? new Dictionary<int, float>())
                    {
                        if (item.Key != 0 && item.Value > 0f) satiation[item.Key] = item.Value;
                    }
                }
            }
            ComponentFlu flu = player.Entity.FindComponent<ComponentFlu>();
            if (flu != null)
            {
                m_parentField.ModifyParentField(flu, "m_fluDuration", MathUtils.Max(record.FluDuration, 0f),
                    typeof(ComponentFlu));
                m_parentField.ModifyParentField(flu, "m_fluOnset", MathUtils.Max(record.FluOnset, 0f),
                    typeof(ComponentFlu));
            }
            ComponentSickness sickness = player.Entity.FindComponent<ComponentSickness>();
            if (sickness != null)
            {
                m_parentField.ModifyParentField(sickness, "m_sicknessDuration",
                    MathUtils.Max(record.SicknessDuration, 0f), typeof(ComponentSickness));
            }
            ComponentOnFire onFire = player.Entity.FindComponent<ComponentOnFire>();
            if (onFire != null)
            {
                if (record.FireDuration > 0f) onFire.SetOnFire(null, record.FireDuration);
                else onFire.Extinguish();
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:703-710（ApplyClothes）
        private static void ApplyClothes(ComponentPlayer player, int[][] clothes)
        {
            if (player == null || clothes == null) return;
            ComponentClothing clothing = player.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return;
            for (int i = 0; i < Math.Min(4, clothes.Length); i++)
                clothing.SetClothes((ClothingSlot)i, clothes[i] ?? Array.Empty<int>());
        }

        // Source: ScMultiplayerProfileHandlers.cs:693-701（CaptureClothes）
        private static int[][] CaptureClothes(ComponentPlayer player)
        {
            int[][] result = PlayerRecordCodec.CreateEmptyClothes();
            ComponentClothing clothing = player?.Entity.FindComponent<ComponentClothing>();
            if (clothing == null) return result;
            for (int i = 0; i < result.Length; i++)
                result[i] = clothing.GetClothes((ClothingSlot)i).ToArray();
            return result;
        }

        // Source: ScMultiplayerProfileHandlers.cs:528-539（CapturePlayerSatiation）
        private Dictionary<int, float> CapturePlayerSatiation(ComponentVitalStats vitalStats)
        {
            if (vitalStats == null) return new Dictionary<int, float>();
            Dictionary<int, float> satiation = m_parentField
                .GetParentField<Dictionary<int, float>>(vitalStats, "m_satiation", typeof(ComponentVitalStats));
            return satiation != null
                ? satiation.Where(item => item.Key != 0 && item.Value > 0f)
                    .ToDictionary(item => item.Key, item => item.Value)
                : new Dictionary<int, float>();
        }

        // Source: Survivalcraft/Game/ComponentCraftingTable.cs:ComponentCraftingTable.UpdateCraftingResult
        // Source: ScMultiplayerProfileHandlers.cs:543-556（CapturePersistentCraftingSlots）
        private void CapturePersistentCraftingSlots(ComponentCraftingTable craftingTable,
            out int[] values, out int[] counts)
        {
            if (craftingTable == null)
            {
                values = Array.Empty<int>();
                counts = Array.Empty<int>();
                return;
            }
            values = CaptureInventoryValues(craftingTable);
            counts = CaptureInventoryCounts(craftingTable);
            values[craftingTable.ResultSlotIndex] = 0;
            counts[craftingTable.ResultSlotIndex] = 0;
        }

        // Source: ScMultiplayerContainerHandlers.cs:743-749
        private static int[] CaptureInventoryValues(IInventory inventory) =>
            Enumerable.Range(0, inventory.SlotsCount)
                .Select(index => NormalizeCrossbowValue(inventory.GetSlotValue(index))).ToArray();

        private static int[] CaptureInventoryCounts(IInventory inventory) =>
            Enumerable.Range(0, inventory.SlotsCount).Select(inventory.GetSlotCount).ToArray();

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Save
        // Source: Survivalcraft/Game/ComponentClothing.cs:ComponentClothing.Save
        // Source: ScMultiplayerProfileHandlers.cs:376-467（CapturePlayerRecord）
        private NetworkPlayerRecord CapturePlayerRecord(PlayerData playerData)
        {
            ComponentPlayer player = playerData?.ComponentPlayer;
            ComponentVitalStats vitalStats = player?.ComponentVitalStats;
            ComponentFlu flu = player?.Entity.FindComponent<ComponentFlu>();
            ComponentSickness sickness = player?.Entity.FindComponent<ComponentSickness>();
            ComponentOnFire onFire = player?.Entity.FindComponent<ComponentOnFire>();
            ComponentCraftingTable handcrafting = player?.Entity.FindComponent<ComponentCraftingTable>();
            SubsystemGameInfo gameInfo = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(false);
            bool globalCreative = gameInfo?.WorldSettings.GameMode == GameMode.Creative;
            var record = new NetworkPlayerRecord
            {
                Name = playerData?.Name ?? "Player",
                PlayerClass = playerData?.PlayerClass ?? PlayerClass.Male,
                SkinName = playerData?.CharacterSkinName ?? string.Empty,
                Position = player?.ComponentBody.Position ?? playerData?.SpawnPosition ?? Vector3.Zero,
                SpawnPosition = playerData?.SpawnPosition ?? Vector3.Zero,
                Level = playerData?.Level ?? PlayerRecordCodec.DefaultLevel,
                Health = player?.ComponentHealth?.Health ?? PlayerRecordCodec.DefaultHealth,
                Air = player?.ComponentHealth?.Air ?? PlayerRecordCodec.DefaultAir,
                Food = vitalStats?.Food ?? PlayerRecordCodec.DefaultFood,
                Stamina = vitalStats?.Stamina ?? PlayerRecordCodec.DefaultStamina,
                Sleep = vitalStats?.Sleep ?? PlayerRecordCodec.DefaultSleep,
                Temperature = vitalStats?.Temperature ?? PlayerRecordCodec.DefaultTemperature,
                TargetTemperature = vitalStats != null
                    ? m_parentField.GetParentField<float>(vitalStats, "m_targetTemperature", typeof(ComponentVitalStats))
                    : PlayerRecordCodec.DefaultTemperature,
                Wetness = vitalStats?.Wetness ?? 0f,
                FluDuration = flu != null
                    ? m_parentField.GetParentField<float>(flu, "m_fluDuration", typeof(ComponentFlu)) : 0f,
                FluOnset = flu != null
                    ? m_parentField.GetParentField<float>(flu, "m_fluOnset", typeof(ComponentFlu)) : 0f,
                SicknessDuration = sickness != null
                    ? m_parentField.GetParentField<float>(sickness, "m_sicknessDuration", typeof(ComponentSickness))
                    : 0f,
                BodyRotation = player?.ComponentBody?.Rotation ?? Quaternion.Identity,
                LookAngles = player?.ComponentLocomotion?.LookAngles ?? Vector2.Zero,
                FireDuration = onFire != null
                    ? m_parentField.GetParentField<float>(onFire, "m_fireDuration", typeof(ComponentOnFire)) : 0f,
                Satiation = CapturePlayerSatiation(vitalStats),
                IsCreativeFlying = globalCreative &&
                    player?.ComponentLocomotion?.IsCreativeFlyEnabled == true,
                HasReceivedInitialItems = true,
                Clothes = CaptureClothes(player)
            };
            // 旧实现在这里还会优先取"失能前保存的普通仓库"（m_capabilityNormalInventories）。
            // 那属于还没移植的"客户端能力/创造仓库切换"功能链 → 这里只用当前仓库，
            // 见报告"未移植"。
            IInventory inventory = player?.ComponentMiner?.Inventory;
            if (inventory is ComponentCreativeInventory creativeInventory)
            {
                record.InventoryWasCreative = globalCreative;
                record.ActiveSlotIndex = creativeInventory.ActiveSlotIndex;
                record.CreativeCategoryIndex = creativeInventory.CategoryIndex;
                record.CreativePageIndex = creativeInventory.PageIndex;
                int slotsCount = Math.Min(creativeInventory.OpenSlotsCount, creativeInventory.SlotsCount);
                record.SlotValues = new int[slotsCount];
                record.SlotCounts = new int[slotsCount];
                for (int i = 0; i < slotsCount; i++)
                {
                    record.SlotValues[i] = creativeInventory.GetSlotValue(i);
                    record.SlotCounts[i] = creativeInventory.GetSlotCount(i);
                }
            }
            else if (inventory != null)
            {
                record.ActiveSlotIndex = inventory.ActiveSlotIndex;
                record.SlotValues = new int[inventory.SlotsCount];
                record.SlotCounts = new int[inventory.SlotsCount];
                for (int i = 0; i < inventory.SlotsCount; i++)
                {
                    record.SlotValues[i] = inventory.GetSlotValue(i);
                    record.SlotCounts[i] = inventory.GetSlotCount(i);
                }
            }
            CapturePersistentCraftingSlots(handcrafting, out record.HandcraftSlotValues,
                out record.HandcraftSlotCounts);
            // 游戏统计由客户端上报，Capture 不覆盖它（调用方保留记录里已有的 Stats）。
            record.Stats = null;
            // 皮肤哈希属于还没移植的"皮肤资源下发"功能链 → 调用方保留记录里已有的哈希。
            record.SkinSha256 = Array.Empty<byte>();
            return record;
        }

        // Source: Survivalcraft/Game/ComponentHealth.cs:ComponentHealth.Load
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Load
        // Source: ScMultiplayerProfileHandlers.cs:1718-1770（ApplyAuthoritativePlayerStats）
        private void ApplyAuthoritativePlayerStats(ComponentPlayer player, float health, float air,
            float food, float stamina, float sleep, float temperature, float wetness, float level)
        {
            if (player == null) return;
            if (player.ComponentHealth != null)
            {
                float safeHealth = MathUtils.Saturate(health);
                m_parentField.ModifyParentField(player.ComponentHealth, "<Health>k__BackingField",
                    safeHealth, typeof(ComponentHealth));
                m_parentField.ModifyParentField(player.ComponentHealth, "<Air>k__BackingField",
                    MathUtils.Saturate(air), typeof(ComponentHealth));
                // ⚠️ 健康为 0 时**不要**写 m_lastHealth：引擎的死亡分支只在
                // Health == 0 && HealthChange < 0 时执行（ComponentHealth.cs:267；
                // HealthChange = Health - m_lastHealth，见 :251）。同步时把 m_lastHealth
                // 一起写成 0 会把这次"跨 0"抹掉 → 死亡分支永不执行。
                if (safeHealth > 0f)
                {
                    m_parentField.ModifyParentField(player.ComponentHealth, "m_lastHealth",
                        safeHealth, typeof(ComponentHealth));
                }
            }
            ComponentVitalStats vital = player.ComponentVitalStats;
            if (vital != null)
            {
                float safeFood = MathUtils.Saturate(food);
                float safeStamina = MathUtils.Saturate(stamina);
                float safeSleep = MathUtils.Saturate(sleep);
                float safeTemperature = MathUtils.Clamp(temperature, 0f, 24f);
                float safeWetness = MathUtils.Saturate(wetness);
                m_parentField.ModifyParentField(vital, "m_food", safeFood, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_stamina", safeStamina, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_sleep", safeSleep, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_temperature", safeTemperature, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_wetness", safeWetness, typeof(ComponentVitalStats));
                // 这些"上一次值"是引擎"跨越阈值才提示一次"的边沿判据
                // （ComponentVitalStats.cs:312/374/445/546/651）。
                m_parentField.ModifyParentField(vital, "m_lastFood", safeFood, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastStamina", safeStamina, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastSleep", safeSleep, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastTemperature", safeTemperature, typeof(ComponentVitalStats));
                m_parentField.ModifyParentField(vital, "m_lastWetness", safeWetness, typeof(ComponentVitalStats));
            }
            player.PlayerData.Level = MathUtils.Max(level, 1f);
        }

        // Source: ScMultiplayerProfileHandlers.cs:1636-1690（ApplyInventory）
        private void ApplyInventory(IInventory inventory, int[] values, int[] counts)
        {
            if (inventory == null || values == null || counts == null) return;
            int slotsCount = Math.Min(inventory.SlotsCount, Math.Min(values.Length, counts.Length));
            ComponentCraftingTable craftingTable = inventory as ComponentCraftingTable;
            bool craftingChanged = false;
            for (int i = 0; i < slotsCount; i++)
            {
                if (craftingTable != null)
                {
                    // Source: Survivalcraft/Game/ComponentCraftingTable.cs:RemoveSlotItems
                    // 权威配方格要原子套用再重算一次：逐槽 Remove/Add 会暴露中间配方、显示假提示。
                    if (i == craftingTable.ResultSlotIndex) continue;
                    int craftingValue = NormalizeCrossbowValue(values[i]);
                    int craftingCount = counts[i];
                    if (craftingCount < 0 || craftingCount > 0 && craftingValue == 0) continue;
                    if (inventory.GetSlotValue(i) == craftingValue && inventory.GetSlotCount(i) == craftingCount)
                        continue;
                    SetCraftingSlotDirect(craftingTable, i, craftingValue, craftingCount);
                    craftingChanged = true;
                    continue;
                }
                int value = NormalizeCrossbowValue(values[i]);
                int count = counts[i];
                if (count < 0 || (count > 0 && value == 0)) continue;
                if (count > 0)
                {
                    int capacity;
                    try
                    {
                        capacity = inventory.GetSlotCapacity(i, value);
                    }
                    catch
                    {
                        continue;
                    }
                    if (count > capacity) continue;
                }
                if (inventory.GetSlotValue(i) == value && inventory.GetSlotCount(i) == count) continue;
                inventory.RemoveSlotItems(i, int.MaxValue);
                if (count > 0) inventory.AddSlotItems(i, value, count);
            }
            if (craftingChanged)
            {
                // Source: Survivalcraft/Game/ComponentCraftingTable.cs:UpdateCraftingResult
                m_parentMethod.InvokeParentMethod(craftingTable, "UpdateCraftingResult");
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:1692-1714（SetCraftingSlotDirect）
        private void SetCraftingSlotDirect(ComponentCraftingTable craftingTable,
            int slotIndex, int value, int count)
        {
            if (craftingTable == null || slotIndex < 0 || slotIndex >= craftingTable.SlotsCount ||
                count < 0 || count > 0 && value == 0)
                return;
            if (count > 0)
            {
                int contents = Game.Terrain.ExtractContents(value);
                if (contents < 0 || contents >= BlocksManager.Blocks.Length) return;
                count = Math.Min(count, BlocksManager.Blocks[contents].MaxStacking);
            }
            IList slots = m_parentField.GetParentField(
                craftingTable, "m_slots", typeof(ComponentInventoryBase)) as IList;
            object slot = slots?[slotIndex];
            if (slot == null) return;
            Type slotType = slot.GetType();
            slotType.GetField("Value", BindingFlags.Instance | BindingFlags.Public)?
                .SetValue(slot, count > 0 ? value : 0);
            slotType.GetField("Count", BindingFlags.Instance | BindingFlags.Public)?
                .SetValue(slot, count);
        }

        // Source: ScMultiplayerContainerHandlers.cs:924-933（NormalizeCrossbowValue）
        private static int NormalizeCrossbowValue(int value)
        {
            if (value == 0 || Game.Terrain.ExtractContents(value) != 200) return value;
            int data = Game.Terrain.ExtractData(value);
            if (!CrossbowBlock.GetArrowType(data).HasValue || CrossbowBlock.GetDraw(data) == 15)
                return value;
            return Game.Terrain.MakeBlockValue(200, 0, CrossbowBlock.SetDraw(data, 15));
        }

        private void Log(string message) => m_diagnostics?.Info(m_ownerId, message);
    }
}

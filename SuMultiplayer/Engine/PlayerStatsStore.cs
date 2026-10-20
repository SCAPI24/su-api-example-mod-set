using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Engine;
using Game;
using GameEntitySystem;
using SuMultiplayer.Modules;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 统计 / 成就的**引擎侧适配器**（实现 <see cref="IPlayerStatsPort"/>）。
    ///
    /// 只做五件事，全部与引擎源码同源：
    ///   ① 读本机 / 联机角色的引擎统计：
    ///      <c>PlayerData.ComponentPlayer.PlayerStats</c> →
    ///      <c>SubsystemPlayerStats.GetPlayerStats(PlayerData.PlayerIndex)</c>
    ///      （Source: Survivalcraft/Game/ComponentCreature.cs:22-37）；
    ///   ② 客户端：把**下载世界带来的主机统计**从本机玩家槽位分出去
    ///      （Source: ScMultiplayerProfileHandlers.cs:1488-1519 SeparateDownloadedPlayerStats）；
    ///   ③ 客户端：把主机**本机身份**记录里的统计写回**本机玩家**的同一个 <c>PlayerStats</c> 对象
    ///      —— 引擎统计面板（GameMenuDialog）读的就是它（Source: Survivalcraft/Game/GameMenuDialog.cs:46）；
    ///   ④ 读写世界作用域角色记录（<c>&lt;世界目录&gt;/SuMultiplayerPlayers.xml</c>）里的统计 + 成就；
    ///   ⑤ 提供记录键 / 身份解析（身份 = <see cref="PlayerIdentity"/>，绝不用 PlayerIndex / clientId 当身份）。
    ///
    /// 字段表用反射枚举 <c>PlayerStats.Stats</c>（引擎 Save/Load 也只认这份 [Stat] 字段），
    /// 因此引擎后续新增统计字段不需要改这里。唯一的例外是 <c>DeathRecordsString</c>：
    /// 它是引擎把 DeathRecords 序列化成字符串的**内部载体**，不是名字 + 值统计，
    /// 不参与同步（否则回填会把死亡记录字段写成一个陈旧字符串）；死亡记录本身按
    /// <see cref="PlayerStatsSnapshot.DeathRecords"/> 单独搬（统计面板会逐条显示）。
    ///
    /// 无静态运行时状态；诊断走注入的 <see cref="IModuleDiagnostics"/>。
    /// </summary>
    public sealed class PlayerStatsStore : IPlayerStatsPort
    {
        /// <summary>引擎的死亡记录序列化载体字段（不是可同步的统计；见类注释）。</summary>
        private const string DeathRecordsStatName = "DeathRecordsString";

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly SuMultiplayerPlayersXml m_records;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly Func<int, string> m_identityResolver;
        private readonly HashSet<string> m_knownStatNames;
        private readonly FieldInfo m_deathRecordsField;

        /// <summary>本机统计槽位已经分离过的世界 / 本机角色（只做一次；换世界重新做）。</summary>
        private Project m_ownershipProject;
        private PlayerData m_ownershipLocal;
        /// <summary>
        /// 已经从本机槽位搬出来的"下载世界自带的主机统计"。
        /// 主机角色（客户端本地的 clientId=0 化身）建出来之前先留在这里，出现后再挂回它的槽位；
        /// 这样主机数据不会停在本机身份的槽位上（统计面板只显示本机身份自己的数据）。
        /// </summary>
        private PlayerStatsSnapshot m_downloadedHostStats;
        private int m_downloadedHostStatsIndex = -1;

        private long m_skippedCaptureFields;
        private long m_unknownApplyFields;
        private long m_createdHostRecords;
        private long m_separatedLocalSlots;
        private long m_downloadedStatsAttached;
        private long m_downloadedStatsPending;

        public PlayerStatsStore(NetworkAvatarSpawner spawner, SuMultiplayerPlayersXml records,
            IModuleDiagnostics diagnostics, Func<int, string> identityResolver, string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_records = records ?? throw new ArgumentNullException(nameof(records));
            m_diagnostics = diagnostics;
            m_identityResolver = identityResolver;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-stats" : ownerId;
            m_knownStatNames = ReadKnownStatNames(diagnostics, m_ownerId);
            m_deathRecordsField = typeof(PlayerStats).GetField("m_deathRecords",
                BindingFlags.Instance | BindingFlags.NonPublic);
        }

        /// <summary>本机身份（UserId.dat；空 = 身份不可用）。</summary>
        public string LocalIdentity => PlayerIdentity.LocalIdentity();

        /// <summary>抓取时因为名字 / 值非法而跳过的字段数（正常构建恒为 0，异常时可见）。</summary>
        public long SkippedCaptureFields => m_skippedCaptureFields;

        /// <summary>回填时本机引擎不认识（跨版本新增 / 删除）的字段数。</summary>
        public long UnknownApplyFields => m_unknownApplyFields;

        public string Describe() =>
            "player-stats-store: knownFields=" + m_knownStatNames.Count +
            " skippedCaptureFields=" + m_skippedCaptureFields +
            " unknownApplyFields=" + m_unknownApplyFields +
            " separatedLocalSlots=" + m_separatedLocalSlots +
            " downloadedStatsAttached=" + m_downloadedStatsAttached +
            " downloadedStatsPending=" + m_downloadedStatsPending +
            " createdHostRecords=" + m_createdHostRecords +
            " records=" + m_records.Count +
            (m_records.LoadFailed ? " recordLoadFailed" : string.Empty) +
            (m_records.SaveFailed ? " recordSaveFailed" : string.Empty);

        // ---------------------------------------------------------------- 本机 / 联机角色

        public bool TryCaptureLocal(out PlayerStatsWorldView view, out string reason)
        {
            view = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进入世界）";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out reason)) return false;
            return TryCapturePlayer(local, 0, out view, out reason);
        }

        /// <summary>
        /// 客户端：下载世界里的 <c>Players/Stats/&lt;index&gt;</c> 是**主机角色**的统计；
        /// 客户端沿用了同一个本地索引 → 统计面板会显示主机数据。这里把那份统计搬走
        /// （挂回主机角色槽位），本机槽位重置为引擎默认值（含死亡记录）。
        /// Source: ScMultiplayerProfileHandlers.cs:1488-1519
        /// </summary>
        public LocalStatsOwnership EnsureLocalStatsOwnership(out string reason)
        {
            reason = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（还没进入世界）";
                return LocalStatsOwnership.NotReady;
            }
            SubsystemPlayerStats subsystem = project.FindSubsystem<SubsystemPlayerStats>(false);
            if (subsystem == null)
            {
                reason = "世界没有 SubsystemPlayerStats（无法把下载的主机统计从本机槽位分离）";
                return LocalStatsOwnership.Failed;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out string localReason))
            {
                reason = localReason;
                return LocalStatsOwnership.NotReady;
            }
            if (!ReferenceEquals(project, m_ownershipProject) ||
                !ReferenceEquals(local, m_ownershipLocal))
            {
                if (!ReferenceEquals(project, m_ownershipProject))
                {
                    // 换世界：上一份待挂回的主机统计属于旧世界，丢掉（新世界会重新分离）。
                    m_downloadedHostStats = null;
                    m_downloadedHostStatsIndex = -1;
                }
                SeparateDownloadedPlayerStats(subsystem, local);
                m_ownershipProject = project;
                m_ownershipLocal = local;
            }
            TryAttachDownloadedHostStats(subsystem, local);
            return LocalStatsOwnership.Owned;
        }

        private void SeparateDownloadedPlayerStats(SubsystemPlayerStats subsystem, PlayerData local)
        {
            int localIndex = local.PlayerIndex;
            PlayerStats slot = subsystem.GetPlayerStats(localIndex);
            // 先把下载来的那份整份抓下来（统计 + 死亡记录），再重置本机槽位。
            // 已经有一份在等主机角色出现时**不覆盖**（本机角色被替换时槽位里已经是本机值，
            // 覆盖会把真正的下载统计丢掉）。
            if (m_downloadedHostStats == null)
            {
                m_downloadedHostStats = PlayerStatsSnapshot.Capture(slot);
                m_downloadedHostStatsIndex = localIndex;
            }
            // 重置成本机身份的"全新角色"：统计字段走引擎默认值，死亡记录清空。
            var defaults = PlayerStatsSnapshot.Capture(new PlayerStats());
            defaults.ApplyTo(slot);
            ClearDeathRecords(slot);
            m_separatedLocalSlots++;
            m_diagnostics?.Info(m_ownerId,
                "客户端：已把下载世界带来的统计从本机玩家槽位分离（PlayerIndex=" + localIndex +
                "，下载值 " + (m_downloadedHostStats?.Values.Count ?? 0) + " 项 / 死亡记录 " +
                (m_downloadedHostStats?.DeathRecords.Count ?? 0) + " 条）→ 本机槽位重置；" +
                "统计面板只显示本机身份自己的数据");
        }

        /// <summary>把暂存的下载统计挂回主机角色（客户端本地的 clientId=0 化身）的槽位。</summary>
        private void TryAttachDownloadedHostStats(SubsystemPlayerStats subsystem, PlayerData local)
        {
            if (m_downloadedHostStats == null) return;
            if (!m_spawner.TryGet(0, out PlayerData hostPlayer) || hostPlayer == null)
            {
                m_downloadedStatsPending++;
                return;
            }
            if (hostPlayer.PlayerIndex == local.PlayerIndex ||
                hostPlayer.PlayerIndex == m_downloadedHostStatsIndex)
            {
                // 主机化身与本机占用同一个索引（不该发生）：不覆盖任何人的槽位，保留在本机侧等下一次。
                m_downloadedStatsPending++;
                return;
            }
            PlayerStats hostSlot = subsystem.GetPlayerStats(hostPlayer.PlayerIndex);
            m_downloadedHostStats.ApplyTo(hostSlot);
            ClearDeathRecords(hostSlot);
            foreach (string record in m_downloadedHostStats.DeathRecords)
                AddDeathRecord(hostSlot, record);
            m_downloadedStatsAttached++;
            m_diagnostics?.Info(m_ownerId,
                "客户端：下载世界带来的主机统计已挂回主机角色槽位（PlayerIndex=" +
                hostPlayer.PlayerIndex + "）；本机槽位（PlayerIndex=" + local.PlayerIndex +
                "）保持本机身份自己的数据");
            m_downloadedHostStats = null;
            m_downloadedHostStatsIndex = -1;
        }

        /// <summary>引擎的死亡记录列表没有公开的 Clear：按引擎字段名清空（统计面板会逐条显示它）。</summary>
        private void ClearDeathRecords(PlayerStats stats)
        {
            if (stats == null || m_deathRecordsField == null) return;
            object value = m_deathRecordsField.GetValue(stats);
            if (value is IList list) list.Clear();
        }

        private static void AddDeathRecord(PlayerStats stats, string text)
        {
            if (stats == null || string.IsNullOrEmpty(text)) return;
            try
            {
                var record = default(PlayerStats.DeathRecord);
                record.Load(text);
                stats.AddDeathRecord(record);
            }
            catch (Exception e)
            {
                Log.Warning("[SuMP] Failed to apply player death record: " + e.Message);
            }
        }

        public bool TryApplyLocal(PlayerStatSet stats, out PlayerStatSet applied, out string reason)
        {
            applied = null;
            if (stats == null)
            {
                reason = "统计为空（null）";
                return false;
            }
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（世界还没加载好）";
                return false;
            }
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out reason)) return false;
            SubsystemPlayerStats playerStats = project.FindSubsystem<SubsystemPlayerStats>(false);
            if (playerStats == null)
            {
                reason = "世界没有 SubsystemPlayerStats（引擎版本 / 世界未初始化）";
                return false;
            }
            PlayerStats engineStats = playerStats.GetPlayerStats(local.PlayerIndex);
            var filtered = new PlayerStatsSnapshot();
            var result = new PlayerStatSet();
            foreach (KeyValuePair<string, string> entry in stats.Entries)
            {
                if (!m_knownStatNames.Contains(entry.Key))
                {
                    m_unknownApplyFields++;
                    continue;
                }
                if (!result.TrySet(entry.Key, entry.Value, out string setReason))
                {
                    reason = "统计 " + entry.Key + " 非法（" + setReason + "）→ 整份回填被拒绝";
                    return false;
                }
                filtered.Values[entry.Key] = entry.Value;
            }
            try
            {
                // 与引擎 Save/Load 同一套字段解析（Source: Survivalcraft/Game/PlayerStats.cs:186-206）。
                filtered.ApplyTo(engineStats);
            }
            catch (Exception e)
            {
                reason = "写本机 PlayerStats 抛异常：" + e.GetType().Name + " " + e.Message;
                return false;
            }
            applied = result;
            reason = null;
            return true;
        }

        /// <summary>
        /// 主机：只抓**主机自己**的统计。
        ///
        /// 为什么不再抓联机角色：主机上的客户端化身不跑客户端的挖掘 / 移动逻辑，它的
        /// <c>PlayerStats</c> 不是那个人的真实进度；而且 <c>PlayerIndex</c> 会被复用，
        /// <c>SubsystemPlayerStats</c> 又不会在角色移除时清表 —— 实机就是从这里把
        /// **世界存档里旧角色的统计**当成客户端身份的数据写进了 SuMultiplayerPlayers.xml。
        /// 客户端自己的统计由该玩家端上报（唯一算得准的地方），见 Modules/PlayerStatsModule。
        /// </summary>
        public bool TryCaptureHost(out PlayerStatsWorldView view, out string reason)
        {
            view = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（主机还没进入世界）";
                return false;
            }
            // 记录表按世界目录加载：先于本适配器的其它记录操作（幂等；同一目录只加载一次）。
            EnsureRecordsLoaded(project);
            if (!m_spawner.TryGetLocalPlayer(project, out PlayerData local, out string localReason))
            {
                reason = "主机本机玩家不可用（" + localReason + "）";
                return false;
            }
            return TryCapturePlayer(local, 0, out view, out reason);
        }

        private bool TryCapturePlayer(PlayerData playerData, int clientId,
            out PlayerStatsWorldView view, out string reason)
        {
            view = null;
            if (playerData == null)
            {
                reason = "没有 PlayerData";
                return false;
            }
            PlayerStats stats = playerData.ComponentPlayer?.PlayerStats;
            if (stats == null)
            {
                // 角色实体还没建好 / 死亡时 ComponentPlayer 为空：直接按 PlayerIndex 取
                // （与 ComponentCreature.PlayerStats 内部完全同源；PlayerIndex 只是本机槽位）。
                SubsystemPlayerStats subsystem = GameManager.Project?
                    .FindSubsystem<SubsystemPlayerStats>(false);
                if (subsystem == null)
                {
                    reason = "世界没有 SubsystemPlayerStats";
                    return false;
                }
                stats = subsystem.GetPlayerStats(playerData.PlayerIndex);
            }
            PlayerStatSet set = CaptureSet(stats);
            string identity = clientId == 0
                ? LocalIdentity
                : (m_identityResolver?.Invoke(clientId) ?? string.Empty);
            string name = (playerData.Name ?? string.Empty).Trim();
            view = new PlayerStatsWorldView(clientId, identity, name,
                BuildRecordKey(identity, name), set);
            reason = null;
            return true;
        }

        private PlayerStatSet CaptureSet(PlayerStats stats)
        {
            var set = new PlayerStatSet();
            PlayerStatsSnapshot snapshot = PlayerStatsSnapshot.Capture(stats);
            foreach (KeyValuePair<string, string> entry in snapshot.Values)
            {
                if (string.Equals(entry.Key, DeathRecordsStatName, StringComparison.Ordinal))
                    continue;
                if (set.TrySet(entry.Key, entry.Value, out string setReason)) continue;
                m_skippedCaptureFields++;
                m_diagnostics?.Warn(m_ownerId, "统计字段 " + entry.Key +
                    " 无法编码（已跳过并计数）：" + setReason);
            }
            return set;
        }

        // ---------------------------------------------------------------- 角色记录

        public PlayerStatsRecordLoad TryLoadRecord(string recordKey, out PlayerStatSet stats,
            out AchievementLedger achievements, out int rejectedAchievements, out string reason)
        {
            stats = null;
            achievements = null;
            rejectedAchievements = 0;
            if (string.IsNullOrEmpty(recordKey))
            {
                reason = "记录键为空";
                return PlayerStatsRecordLoad.Failed;
            }
            if (!EnsureRecordsLoadedForRead(recordKey, out reason))
                return PlayerStatsRecordLoad.Failed;
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record))
            {
                reason = "角色记录里没有这个身份（可能是新玩家 / 旧记录）";
                return PlayerStatsRecordLoad.Missing;
            }
            stats = ToSet(record.Stats);
            achievements = record.Achievements?.Clone() ?? new AchievementLedger();
            rejectedAchievements = record.RejectedAchievements;
            reason = null;
            return PlayerStatsRecordLoad.Loaded;
        }

        public bool StoreRecord(string recordKey, PlayerStatSet stats,
            AchievementLedger achievements, out string reason)
        {
            if (string.IsNullOrEmpty(recordKey))
            {
                reason = "记录键为空";
                return false;
            }
            if (!EnsureRecordsLoadedForRead(recordKey, out reason)) return false;
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record) || record == null)
            {
                // 只有主机自己的记录允许在这里补建（联机角色的记录由联机角色模块创建，
                // 这里绝不伪造一条缺名字 / 外观的半记录）。
                if (!TryCreateHostLocalRecord(recordKey, out record, out reason)) return false;
            }
            record.Stats = ToSnapshot(stats);
            record.Achievements = achievements?.Clone() ?? new AchievementLedger();
            if (!m_records.Put(recordKey, record))
            {
                reason = "角色记录拒绝写入（本端不是主机 / 记录键为空）";
                return false;
            }
            reason = null;
            return true;
        }

        public bool FlushRecords(out string reason)
        {
            if (m_records.LoadFailed)
            {
                reason = "角色记录文件先前解析失败，本会话拒绝写盘（原文件保持原样）";
                return false;
            }
            m_records.SaveIfDirty();
            if (m_records.SaveFailed)
            {
                reason = "写 SuMultiplayerPlayers.xml 失败（详细原因见 player-avatar / player-stats 日志）";
                return false;
            }
            reason = null;
            return true;
        }

        public string BuildRecordKey(string identity, string name) =>
            PlayerIdentity.RecordKey(identity, name);

        // ---------------------------------------------------------------- 内部

        private bool EnsureRecordsLoadedForRead(string recordKey, out string reason)
        {
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project（记录表还没有世界目录）";
                return false;
            }
            EnsureRecordsLoaded(project);
            if (m_records.LoadFailed)
            {
                reason = "角色记录文件损坏，本会话只读内存并拒绝写盘（记录键 " +
                    PlayerStatsModule.DescribeKey(recordKey) + "）";
                return false;
            }
            if (m_records.WorldDirectory.Length == 0)
            {
                reason = "记录表还没有绑定世界目录（SubsystemGameInfo 不可用？）";
                return false;
            }
            reason = null;
            return true;
        }

        private void EnsureRecordsLoaded(Project project)
        {
            SubsystemGameInfo gameInfo = project?.FindSubsystem<SubsystemGameInfo>(false);
            m_records.EnsureLoaded(gameInfo?.DirectoryName, isHost: true);
        }

        private bool TryCreateHostLocalRecord(string recordKey, out NetworkPlayerRecord record,
            out string reason)
        {
            record = null;
            Project project = GameManager.Project;
            if (project == null)
            {
                reason = "没有加载中的 Project";
                return false;
            }
            if (!m_spawner.TryCaptureLocalProfile(out PlayerProfileSnapshot profile,
                    out string profileReason))
            {
                reason = "主机本机资料不可用，无法补建角色记录：" + profileReason;
                return false;
            }
            string expectedKey = PlayerIdentity.RecordKey(LocalIdentity, profile.Name);
            if (!string.Equals(expectedKey, recordKey, StringComparison.Ordinal))
            {
                reason = "记录键 " + PlayerStatsModule.DescribeKey(recordKey) +
                    " 不是主机本机身份的记录键（联机角色的记录必须由联机角色模块创建）";
                return false;
            }
            m_spawner.TryGetLocalPlayer(project, out PlayerData local, out _);
            Vector3 position = local?.ComponentPlayer?.ComponentBody?.Position ??
                local?.SpawnPosition ?? Vector3.Zero;
            record = new NetworkPlayerRecord
            {
                Name = profile.Name,
                NameConfirmed = true,
                PlayerClass = NetworkAvatarSpawner.ToPlayerClass(profile.Appearance.Sex),
                SkinName = profile.Appearance.SkinName ?? string.Empty,
                SkinSha256 = profile.Appearance.SkinSha256 != null
                    ? (byte[])profile.Appearance.SkinSha256.Clone()
                    : Array.Empty<byte>(),
                Clothes = PlayerAppearance.CloneClothes(profile.Appearance.Clothes),
                Position = position,
                SpawnPosition = Vector3.Zero,
                Level = PlayerRecordCodec.DefaultLevel,
                Health = PlayerRecordCodec.DefaultHealth,
                HasReceivedInitialItems = true
            };
            m_createdHostRecords++;
            m_diagnostics?.Info(m_ownerId, "为主机本机身份补建角色记录（统计 / 成就需要世界作用域持久化）：" +
                PlayerStatsModule.DescribeKey(recordKey));
            reason = null;
            return true;
        }

        private static PlayerStatSet ToSet(PlayerStatsSnapshot snapshot)
        {
            var set = new PlayerStatSet();
            if (snapshot == null) return set;
            foreach (KeyValuePair<string, string> entry in snapshot.Values)
            {
                if (string.Equals(entry.Key, DeathRecordsStatName, StringComparison.Ordinal))
                    continue;
                set.TrySet(entry.Key, entry.Value, out _);
            }
            return set;
        }

        private static PlayerStatsSnapshot ToSnapshot(PlayerStatSet set)
        {
            var snapshot = new PlayerStatsSnapshot();
            if (set == null) return snapshot;
            foreach (KeyValuePair<string, string> entry in set.Entries)
                snapshot.Values[entry.Key] = entry.Value;
            return snapshot;
        }

        private static HashSet<string> ReadKnownStatNames(IModuleDiagnostics diagnostics,
            string ownerId)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var probe = new PlayerStats();
                foreach (FieldInfo field in probe.Stats)
                {
                    if (string.Equals(field.Name, DeathRecordsStatName, StringComparison.Ordinal))
                        continue;
                    names.Add(field.Name);
                }
            }
            catch (Exception e)
            {
                diagnostics?.Error(ownerId,
                    "枚举 PlayerStats 的 [Stat] 字段失败（统计回填会全部跳过）：" + e.Message, e);
            }
            return names;
        }
    }
}

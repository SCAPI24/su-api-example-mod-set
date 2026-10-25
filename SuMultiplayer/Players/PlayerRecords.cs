using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Engine;
using Game;
using SuMultiplayer.Modules;

namespace SuMultiplayer.Players
{
    /// <summary>
    /// 联机角色记录：**只属于联机的角色数据**，与世界的 Project.xml 完全分开。
    ///
    /// Source: Mod/ScMultiplayer/Core/ScMultiplayerSupportTypes.cs:158-202（字段全集，逐字段照搬）
    /// </summary>
    public class NetworkPlayerRecord
    {
        public string Name;
        /// <summary>
        /// 这个名字是否来自**已确认的真实资料**（角色创建屏 / 客户端 Hello.profile）。
        /// 旧构建把引擎默认显示名 "Android User" 直接当角色名写进记录；这样的记录必须做一次
        /// 显式迁移（要求客户端重新创建角色），不能把默认名当成真实玩家名字。
        /// 新记录一律 true；读旧 XML 时缺失属性 = 按"名字是否为引擎默认名"推断（默认名 → false）。
        /// </summary>
        public bool NameConfirmed = true;
        public PlayerClass PlayerClass;
        public string SkinName;
        public byte[] SkinSha256 = Array.Empty<byte>();
        /// <summary>
        /// 自定义皮肤的**原始资源字节**（世界作用域，随本记录写进 SuMultiplayerPlayers.xml）。
        /// 主机会在玩家加入 / 重连时直接补发，客户端不需要重新上传；内置皮肤 / 旧记录为空。
        /// 只属于主机记录，绝不写客户端全局皮肤库。
        /// </summary>
        public byte[] SkinAsset = Array.Empty<byte>();
        public Vector3 Position;
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPosition
        public Vector3 SpawnPosition;
        public float Level = 1f;
        public float Health = 1f;
        public float Air = 1f;
        public float Food = 0.9f;
        public float Stamina = 1f;
        public float Sleep = 0.9f;
        public float Temperature = 12f;
        public float TargetTemperature = 12f;
        public float Wetness;
        public float FluDuration;
        public float FluOnset;
        public float SicknessDuration;
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.Save
        // Source: Survivalcraft/Game/ComponentVitalStats.cs:ComponentVitalStats.Save
        // Source: Survivalcraft/Game/ComponentOnFire.cs:ComponentOnFire.Save
        public Quaternion BodyRotation = Quaternion.Identity;
        public Vector2 LookAngles;
        public float FireDuration;
        public Dictionary<int, float> Satiation = new Dictionary<int, float>();
        public bool IsCreativeFlying;
        public bool HasReceivedInitialItems = true;
        public bool InventoryWasCreative;
        public int ActiveSlotIndex;
        public int CreativeCategoryIndex;
        public int CreativePageIndex;
        public int[] SlotValues;
        public int[] SlotCounts;
        // Source: Survivalcraft/Game/ComponentCraftingTable.cs:ComponentCraftingTable.Save
        public int[] HandcraftSlotValues;
        public int[] HandcraftSlotCounts;
        public int[][] Clothes;
        // 联机角色的**游戏统计**：由客户端周期性上报（统计只有客户端算得准），
        // 存进本记录（SuMultiplayerPlayers.xml）而不是 Project.xml。
        // Source: Survivalcraft/Game/SubsystemPlayerStats.cs:SubsystemPlayerStats
        public PlayerStatsSnapshot Stats;
        /// <summary>
        /// 主机权威的成就账本（id + UTC 解锁时间）。引擎没有成就子系统，
        /// 成就由 SuMultiplayer 目录按权威统计判定；记录按**稳定身份键**保存，
        /// 因此退出重进 / 重连后成就不会丢。未知 id 原样保留（前向兼容）。
        /// </summary>
        public AchievementLedger Achievements;
        /// <summary>
        /// 读取记录时被拒绝的成就条目数（非法 id / 负时间戳 / 超上限）。只用于诊断，
        /// 不写入 XML；&gt;0 时 EnsureLoaded 会写一条明确日志。
        /// </summary>
        public int RejectedAchievements;

        /// <summary>
        /// 这个角色记录**不常变内容**的独立哈希（算法 / 参与字段见 <see cref="PlayerRecordHash"/>）。
        /// 随记录写进 <c>SuMultiplayerPlayers.xml</c>（**绝不写进 Project.xml**）：世界 pak 里带着
        /// 这份文件，加入端本地也有一份，重连时用它做"哪些角色记录变了"的 diff。
        /// 0 = 未知（旧文件没有该属性 / 解析失败）：调用方必须重算后写回，不能当成"没变"。
        /// </summary>
        public ulong RecordHash;
    }

    /// <summary>
    /// "某个角色记录的 hash"（身份键 → hash）的纯值载体。
    /// Hello 里由客户端上报"我本地已有的角色记录 hash"，主机据此只重发变化了的角色记录。
    /// </summary>
    public readonly struct RecordHashRef
    {
        /// <summary>角色记录键（<see cref="PlayerIdentity.RecordKey(string,string,int)"/>）。</summary>
        public readonly string RecordKey;
        public readonly ulong Hash;

        public RecordHashRef(string recordKey, ulong hash)
        {
            RecordKey = recordKey ?? string.Empty;
            Hash = hash;
        }
    }

    /// <summary>
    /// 角色记录的取值编解码。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Player/PlayerProfileValueCodec.cs:12-37
    /// Source: Mod/ScMultiplayer/Modules/Player/PlayerRecordValuePolicy.cs:11-34
    /// Source: Mod/ScMultiplayer/Modules/Player/SkinHashCodec.cs:10-33
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:559-573
    ///         （LooksLikeLegacyCreativeInventory）
    /// 纯值编码，不持有任何状态；玩家状态的所有权仍在记录表与角色创建器手里。
    /// </summary>
    internal static class PlayerRecordCodec
    {
        public const float DefaultLevel = 1f;
        public const float DefaultHealth = 1f;
        public const float DefaultAir = 1f;
        public const float DefaultFood = 0.9f;
        public const float DefaultStamina = 1f;
        public const float DefaultSleep = 0.9f;
        public const float DefaultTemperature = 12f;

        public static int[][] CreateEmptyClothes() =>
            new[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        public static string FormatFloat(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        public static float ParseFloat(string value, float fallback = 0f) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result : fallback;

        public static PlayerClass ParsePlayerClass(string value) =>
            Enum.TryParse(value, true, out PlayerClass result) ? result : PlayerClass.Male;

        public static bool ParseBool(string value, bool fallback) =>
            bool.TryParse(value, out bool result) ? result : fallback;

        public static string FormatIntArray(int[] values) =>
            values == null || values.Length == 0 ? string.Empty : string.Join(";", values);

        public static int[] ParseIntArray(string values)
        {
            if (string.IsNullOrWhiteSpace(values)) return Array.Empty<int>();
            return values.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => int.TryParse(value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int result) ? result : 0).ToArray();
        }

        public static bool ShouldPersistItem(int value, int count) => value != 0 && count > 0;

        public static byte[] CloneSkinHash(byte[] bytes) =>
            bytes == null || bytes.Length == 0 ? Array.Empty<byte>() : (byte[])bytes.Clone();

        public static bool IsSkinHashValid(byte[] hash) =>
            hash != null && hash.Length == 32 && hash.Any(value => value != 0);

        public static string FormatSkinHash(byte[] hash) =>
            IsSkinHashValid(hash) ? Convert.ToHexString(hash).ToLowerInvariant() : string.Empty;

        public static byte[] ParseSkinHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<byte>();
            string text = value.Trim();
            if (text.Length != 64) return Array.Empty<byte>();
            try
            {
                byte[] hash = Convert.FromHexString(text);
                return IsSkinHashValid(hash) ? hash : Array.Empty<byte>();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        // Source: Survivalcraft/Game/ComponentCreativeInventory.cs:ComponentCreativeInventory.GetSlotCount
        public static bool LooksLikeLegacyCreativeInventory(NetworkPlayerRecord record)
        {
            if (record == null) return false;
            if (record.InventoryWasCreative) return false;
            int slotsCount = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
            if (slotsCount > 64) return true;
            int creativeStacks = 0;
            for (int i = 0; i < slotsCount; i++)
            {
                if (record.SlotCounts[i] >= 9999 && ++creativeStacks >= 8) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 联机角色记录文件：<c>&lt;世界目录&gt;/SuMultiplayerPlayers.xml</c>。
    ///
    /// 契约（照旧实现）：
    ///   · **只有主机读写**（<see cref="EnsureLoaded"/> 的 isHost 与 <see cref="Put"/> 的守卫）；
    ///     客户端只从握手里拿身份，不去读/写这份文件。
    ///   · 文件不存在 = 空表，不报错（第一次联机时本来就没有）；
    ///   · 文件损坏 = 明确 Log.Error + 保持空表，并且**本会话拒绝写盘** —— 不做"解析失败就当空表、
    ///     然后把空表写回去"的静默改写（那会把玩家所有人的角色记录一次抹掉）；
    ///   · 写盘时机由调用方决定（旧实现：每 5 秒周期刷新 + 角色创建/离开时）。
    ///
    /// Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:126-372
    ///         （EnsurePlayerRecordsLoaded / SavePlayerRecords，路径、根节点、Version、字段顺序全部照搬）
    /// </summary>
    public sealed class SuMultiplayerPlayersXml
    {
        public const string FileName = "SuMultiplayerPlayers.xml";
        // v9：角色记录追加 RecordHash（不常变内容的独立哈希，用于重连时的角色记录 diff）。
        // v8：角色记录追加 <Achievements>（主机权威成就账本，id + UTC 解锁时间）。
        // v7：角色记录追加 SkinAsset（自定义皮肤资源，世界作用域）。
        public const int Version = 9;

        private readonly string m_ownerId;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly Dictionary<string, NetworkPlayerRecord> m_records =
            new Dictionary<string, NetworkPlayerRecord>(StringComparer.Ordinal);
        private string m_worldDirectory = string.Empty;
        private bool m_dirty;
        private bool m_isHost;
        private bool m_loadFailed;
        /// <summary>本次加载里 RecordHash 被重算（缺失 / 不一致）的记录数（诊断；随加载日志写一行）。</summary>
        private int m_hashRepairs;

        public SuMultiplayerPlayersXml(string ownerId, IModuleDiagnostics diagnostics)
        {
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-records" : ownerId;
            m_diagnostics = diagnostics;
        }

        /// <summary>已加载的角色记录数（空表 = 0）。</summary>
        public int Count => m_records.Count;

        /// <summary>当前记录表所属的世界目录（空 = 还没进世界）。</summary>
        public string WorldDirectory => m_worldDirectory;

        /// <summary>本次加载是否失败（损坏文件）。为 true 时本会话拒绝写盘。</summary>
        public bool LoadFailed => m_loadFailed;

        /// <summary>最近一次写盘是否失败（失败后保持 dirty，下一次 SaveIfDirty 会重试）。</summary>
        public bool SaveFailed { get; private set; }

        public IEnumerable<KeyValuePair<string, NetworkPlayerRecord>> Entries => m_records;

        public bool TryGet(string recordKey, out NetworkPlayerRecord record) =>
            m_records.TryGetValue(recordKey ?? string.Empty, out record);

        /// <summary>
        /// 取某条角色记录的**不常变内容 hash**（0 = 没有这条记录 / 还没算）。
        /// 名册 / HelloAck 下发 `record_hash`、皮肤资源补发前做 diff 都用它。
        /// </summary>
        public bool TryGetRecordHash(string recordKey, out ulong hash)
        {
            hash = 0UL;
            if (!m_records.TryGetValue(recordKey ?? string.Empty, out NetworkPlayerRecord record) ||
                record == null) return false;
            // 现算而不是读缓存字段：调用方可能在 Put 之后又改了记录字段（资料 / 背包落地），
            // 读旧缓存会把"变了"报成"没变"，导致重连时漏发。
            hash = PlayerRecordHash.Compute(record);
            return true;
        }

        /// <summary>按 (身份, 名字, 槽位) 取记录 hash（槽位 0 = 旧口径）。</summary>
        public bool TryGetRecordHash(string identity, string name, int slot, out ulong hash) =>
            TryGetRecordHash(PlayerIdentity.RecordKey(identity, name, slot), out hash);

        /// <summary>
        /// **只读**地从任意世界目录的 `SuMultiplayerPlayers.xml` 读出"身份键 → hash"清单。
        /// 客户端用它报告"我本地这份 pak 副本里已有的角色记录 hash"（客户端不拥有记录表，只读）。
        /// 文件不存在 = 空清单（首次加入，不是错误）；解析失败 = false + reason（调用方明确计数）。
        /// 没有 RecordHash 属性的旧记录 → hash 0（= 未知，上报时跳过，主机按"变了"处理）。
        /// </summary>
        public static bool TryReadRecordHashes(string worldDirectory,
            List<RecordHashRef> target, out string reason)
        {
            reason = null;
            if (target == null)
            {
                reason = "调用方没有给清单容器";
                return false;
            }
            target.Clear();
            if (string.IsNullOrEmpty(worldDirectory))
            {
                reason = "世界目录为空（没进世界 / 不是下载世界）";
                return false;
            }
            string path = Storage.CombinePaths(worldDirectory, FileName);
            if (!Storage.FileExists(path)) return true;   // 首次加入：本地没有文件 = 空清单
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                foreach (XElement element in document.Root?.Elements("Player") ??
                    Enumerable.Empty<XElement>())
                {
                    string identity = (string)element.Attribute("Identity");
                    if (string.IsNullOrWhiteSpace(identity)) continue;
                    ulong hash = PlayerRecordHash.Parse((string)element.Attribute("RecordHash"));
                    if (hash == 0UL) continue;   // 旧记录没有 hash：不冒充"已知内容"
                    target.Add(new RecordHashRef(identity, hash));
                }
                return true;
            }
            catch (Exception ex)
            {
                target.Clear();
                reason = "读取本地角色记录 hash 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 换 Project（换世界 / 删掉旧世界后在同一路径新建）时调用：
        /// 先落盘当前世界的记录，再清空内存表并忘掉世界目录。
        /// **不能只按目录字符串判断是否换世界**：世界被删掉后在同一个目录名上重新创建
        /// （引擎的 New World 会复用 `data:/Worlds/World`），只比路径会让上一个世界的
        /// 角色记录（名字/皮肤/统计/成就）漏进新世界 —— 客户端因此被当成"已有角色"，
        /// 不弹创建角色界面。
        /// </summary>
        public void OnProjectChanged()
        {
            try
            {
                if (m_isHost && m_dirty) SaveIfDirty();
            }
            catch (Exception e)
            {
                m_diagnostics?.Warn(m_ownerId, "换世界前落盘角色记录失败：" + e.GetType().Name +
                    " " + e.Message + "（内存表仍会清空，避免上一个世界的数据漏进新世界）");
            }
            m_records.Clear();
            m_worldDirectory = string.Empty;
            m_dirty = false;
            m_isHost = false;
            m_loadFailed = false;
            SaveFailed = false;
            m_hashRepairs = 0;
        }

        /// <summary>
        /// 世界目录变化时重新加载（同一个目录只加载一次）。
        /// Source: ScMultiplayerProfileHandlers.cs:126-271
        /// </summary>
        public void EnsureLoaded(string worldDirectory, bool isHost)
        {
            if (!isHost) return;
            if (string.IsNullOrEmpty(worldDirectory) ||
                string.Equals(worldDirectory, m_worldDirectory, StringComparison.OrdinalIgnoreCase))
                return;

            m_records.Clear();
            m_worldDirectory = worldDirectory;
            m_isHost = true;
            m_dirty = false;
            m_loadFailed = false;
            SaveFailed = false;

            string path = Storage.CombinePaths(worldDirectory, FileName);
            if (!Storage.FileExists(path)) return;   // 没有文件 = 空表，不是错误
            try
            {
                XDocument document;
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read))
                    document = XDocument.Load(stream);
                foreach (XElement element in document.Root?.Elements("Player") ?? Enumerable.Empty<XElement>())
                {
                    string identity = (string)element.Attribute("Identity");
                    if (string.IsNullOrWhiteSpace(identity)) continue;
                    NetworkPlayerRecord record = ReadPlayer(element, out string skinAssetReason);
                    if (!string.IsNullOrEmpty(skinAssetReason))
                    {
                        m_diagnostics?.Warn(m_ownerId, "记录 " +
                            PlayerIdentity.Describe((string)element.Attribute("Identity")) +
                            " 的皮肤资源无法读取：" + skinAssetReason + "（仅保留哈希）");
                    }
                    // Legacy records did not persist SpawnPosition. Keep their previous saved
                    // position as the respawn anchor instead of falling back to the creation point.
                    // Source: ScMultiplayerProfileHandlers.cs:197-200
                    if (record.SpawnPosition == Vector3.Zero) record.SpawnPosition = record.Position;
                    ReadInventory(element.Element("Inventory"), out int[] slotValues, out int[] slotCounts);
                    record.SlotValues = slotValues;
                    record.SlotCounts = slotCounts;
                    ReadHandcrafting(element.Element("Handcrafting"), record);
                    ReadSatiation(element.Element("Satiation"), record);
                    ReadClothes(element.Element("Clothes"), record);
                    ValidateLoadedSkinAsset(element, record);
                    record.Stats = PlayerStatsSnapshot.FromXml(element.Element("Stats"));
                    // 成就账本（v8）：未知 id 原样保留；非法 / 超上限的条目明确计数 + 记日志。
                    record.Achievements = AchievementLedger.FromXml(
                        element.Element("Achievements"), out int rejectedAchievements);
                    record.RejectedAchievements = rejectedAchievements;
                    if (rejectedAchievements > 0)
                    {
                        m_diagnostics?.Warn(m_ownerId, "记录 " + PlayerIdentity.Describe(identity) +
                            " 里有 " + rejectedAchievements + " 条成就不合法（非法 id / 负时间戳 / 超上限）" +
                            "→ 已丢弃并计数（其余条目保留）");
                    }
                    if (element.Attribute("InitialItems") == null)
                    {
                        bool hasClothes = record.Clothes.Any(slot => slot != null && slot.Length > 0);
                        record.HasReceivedInitialItems = hasClothes;
                    }
                    // 旧版本创造仓库的迁移：**显式记一条日志**，不做无声改写。
                    // Source: ScMultiplayerProfileHandlers.cs:240-247
                    if (!record.InventoryWasCreative && PlayerRecordCodec.LooksLikeLegacyCreativeInventory(record))
                    {
                        m_diagnostics?.Info(m_ownerId, "记录 " + PlayerIdentity.Describe(identity) +
                            " 是旧版本的创造仓库格式：改为创造仓库记录并清空槽位（会在角色创建时按创造仓库恢复）");
                        record.InventoryWasCreative = true;
                        record.SlotValues = Array.Empty<int>();
                        record.SlotCounts = Array.Empty<int>();
                        m_dirty = true;
                    }
                    // 每角色独立 hash（v9）：与本次加载到的**不常变**字段重算值比对。
                    // 缺失（v8 及更早的文件）/ 不一致（文件被外部改过 / 字段集合升级）→ 用重算值覆盖并记一条
                    // 明确日志 + 计数，随后落盘修正（绝不把"未知"当成"没变"去跳过重发）。
                    ulong storedHash = PlayerRecordHash.Parse((string)element.Attribute("RecordHash"));
                    ulong computedHash = PlayerRecordHash.Compute(record);
                    if (storedHash != computedHash)
                    {
                        m_hashRepairs++;
                        m_diagnostics?.Info(m_ownerId, "记录 " + PlayerIdentity.Describe(identity) +
                            " 的 RecordHash " + (storedHash == 0UL ? "缺失（旧版本文件）" :
                                "与内容不一致（" + PlayerRecordHash.Format(storedHash) + "）") +
                            " → 已按记录内容重算为 " + PlayerRecordHash.Format(computedHash) +
                            " 并会随下次落盘修正");
                        m_dirty = true;
                    }
                    record.RecordHash = computedHash;
                    m_records[identity] = record;
                }
                m_diagnostics?.Info(m_ownerId, "已加载联机角色记录 " + m_records.Count + " 条：" + path +
                    (m_hashRepairs > 0 ? "（其中 " + m_hashRepairs + " 条 RecordHash 已重算）" : string.Empty));
                if (m_dirty) SaveIfDirty();
            }
            catch (Exception ex)
            {
                // 明确失败：保持空表 + 本会话拒绝写盘（绝不把损坏文件覆盖成空表）。
                m_records.Clear();
                m_dirty = false;
                m_loadFailed = true;
                m_diagnostics?.Error(m_ownerId,
                    "联机角色记录文件损坏，解析失败；本会话保持空表并且不写盘（原文件保持原样）：" + path,
                    ex);
            }
        }

        /// <summary>写入一条记录（主机专属）。</summary>
        public bool Put(string recordKey, NetworkPlayerRecord record)
        {
            if (!m_isHost)
            {
                m_diagnostics?.Error(m_ownerId,
                    "拒绝写角色记录：本端不是主机（只有主机写 SuMultiplayerPlayers.xml）", null);
                return false;
            }
            if (record == null || string.IsNullOrEmpty(recordKey)) return false;
            // hash 与记录内容同步：名册 / HelloAck 下发 record_hash 时直接读它，不必等落盘。
            record.RecordHash = PlayerRecordHash.Compute(record);
            m_records[recordKey] = record;
            m_dirty = true;
            return true;
        }

        /// <summary>
        /// 有改动就落盘（主机专属）。
        /// Source: ScMultiplayerProfileHandlers.cs:273-372
        /// </summary>
        public bool SaveIfDirty()
        {
            if (!m_isHost || !m_dirty) return false;
            if (string.IsNullOrEmpty(m_worldDirectory)) return false;
            if (m_loadFailed)
            {
                m_diagnostics?.Error(m_ownerId,
                    "拒绝写盘：本世界的角色记录文件先前解析失败，覆盖它会丢掉原有记录（原文件保持原样）",
                    null);
                return false;
            }
            try
            {
                var root = new XElement("SuMultiplayerPlayers", new XAttribute("Version", Version));
                foreach (KeyValuePair<string, NetworkPlayerRecord> item in
                    m_records.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    NetworkPlayerRecord record = item.Value;
                    if (record == null) continue;
                    // 落盘前重算：hash 必须与**文件里真正写出的内容**一致（调用方可能直接改了记录字段
                    // 而没走 Put；不重算就会出现"文件内容变了、hash 还是旧的"→ 重连 diff 漏发）。
                    record.RecordHash = PlayerRecordHash.Compute(record);
                    root.Add(WritePlayer(item.Key, record));
                }
                string path = Storage.CombinePaths(m_worldDirectory, FileName);
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create))
                    new XDocument(root).Save(stream);
                m_dirty = false;
                SaveFailed = false;
                return true;
            }
            catch (Exception ex)
            {
                // 保持 dirty = true（下一次 SaveIfDirty 会重试），并让调用方看到明确失败。
                SaveFailed = true;
                m_diagnostics?.Error(m_ownerId,
                    "写联机角色记录失败：" + Storage.CombinePaths(m_worldDirectory, FileName), ex);
                return false;
            }
        }

        /// <summary>
        /// 记录里的皮肤资源校验：大小 / PNG / 哈希必须自洽，否则**明确记日志并丢弃资源**
        /// （哈希/名称保留，其他端会显示占位并计数；客户端可显式重传）。绝不静默使用损坏资源。
        /// </summary>
        private void ValidateLoadedSkinAsset(XElement element, NetworkPlayerRecord record)
        {
            if (record.SkinAsset == null || record.SkinAsset.Length == 0) return;
            if (!PlayerRecordCodec.IsSkinHashValid(record.SkinSha256))
            {
                m_diagnostics?.Warn(m_ownerId, "记录 " + PlayerIdentity.Describe(
                    (string)element.Attribute("Identity")) + " 有皮肤资源但没有合法哈希 → 丢弃资源");
                record.SkinAsset = Array.Empty<byte>();
                return;
            }
            if (!SkinAssetPolicy.TryValidateAssetData(record.SkinAsset, out string reason))
            {
                m_diagnostics?.Warn(m_ownerId, "记录 " + PlayerIdentity.Describe(
                    (string)element.Attribute("Identity")) + " 的皮肤资源非法（" + reason +
                    "）→ 丢弃资源，仅保留哈希（其他端会显示占位并计数）");
                record.SkinAsset = Array.Empty<byte>();
                return;
            }
            byte[] actual = SkinAssetPolicy.Sha256(record.SkinAsset);
            if (!SkinAssetPolicy.BytesEqual(actual, record.SkinSha256))
            {
                m_diagnostics?.Warn(m_ownerId, "记录 " + PlayerIdentity.Describe(
                    (string)element.Attribute("Identity")) + " 的皮肤资源哈希不一致（实际 " +
                    SkinAssetPolicy.HashHex(actual) + " ≠ 记录 " +
                    SkinAssetPolicy.HashHex(record.SkinSha256) + "）→ 丢弃资源");
                record.SkinAsset = Array.Empty<byte>();
            }
        }

        // ---------------------------------------------------------------- XML（逐字段对照旧实现）

        // Source: ScMultiplayerProfileHandlers.cs:148-262
        private static NetworkPlayerRecord ReadPlayer(XElement element, out string skinAssetReason)
        {
            byte[] skinHash = PlayerRecordCodec.ParseSkinHash((string)element.Attribute("SkinSha256"));
            return new NetworkPlayerRecord
            {
            Name = (string)element.Attribute("Name") ?? "Player",
            NameConfirmed = PlayerRecordCodec.ParseBool((string)element.Attribute("NameConfirmed"),
                !PlayerIdentity.IsEngineDefaultName((string)element.Attribute("Name"))),
            PlayerClass = PlayerRecordCodec.ParsePlayerClass((string)element.Attribute("Class")),
            SkinName = (string)element.Attribute("Skin") ?? string.Empty,
            SkinSha256 = skinHash,
            SkinAsset = ReadSkinAsset(element.Element("SkinAsset"), out skinAssetReason),
            Position = new Vector3(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("X")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("Y")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("Z"))),
            SpawnPosition = new Vector3(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("SpawnX")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("SpawnY")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("SpawnZ"))),
            Level = PlayerRecordCodec.ParseFloat((string)element.Attribute("Level"), PlayerRecordCodec.DefaultLevel),
            Health = PlayerRecordCodec.ParseFloat((string)element.Attribute("Health"), PlayerRecordCodec.DefaultHealth),
            Air = PlayerRecordCodec.ParseFloat((string)element.Attribute("Air"), PlayerRecordCodec.DefaultAir),
            Food = PlayerRecordCodec.ParseFloat((string)element.Attribute("Food"), PlayerRecordCodec.DefaultFood),
            Stamina = PlayerRecordCodec.ParseFloat((string)element.Attribute("Stamina"), PlayerRecordCodec.DefaultStamina),
            Sleep = PlayerRecordCodec.ParseFloat((string)element.Attribute("Sleep"), PlayerRecordCodec.DefaultSleep),
            Temperature = PlayerRecordCodec.ParseFloat((string)element.Attribute("Temperature"), PlayerRecordCodec.DefaultTemperature),
            TargetTemperature = PlayerRecordCodec.ParseFloat(
                (string)element.Attribute("TargetTemperature"), PlayerRecordCodec.DefaultTemperature),
            Wetness = PlayerRecordCodec.ParseFloat((string)element.Attribute("Wetness")),
            FluDuration = PlayerRecordCodec.ParseFloat((string)element.Attribute("FluDuration")),
            FluOnset = PlayerRecordCodec.ParseFloat((string)element.Attribute("FluOnset")),
            SicknessDuration = PlayerRecordCodec.ParseFloat((string)element.Attribute("SicknessDuration")),
            BodyRotation = new Quaternion(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQX")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQY")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQZ")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("BodyQW"), 1f)),
            LookAngles = new Vector2(
                PlayerRecordCodec.ParseFloat((string)element.Attribute("LookX")),
                PlayerRecordCodec.ParseFloat((string)element.Attribute("LookY"))),
            FireDuration = PlayerRecordCodec.ParseFloat((string)element.Attribute("FireDuration")),
            IsCreativeFlying = PlayerRecordCodec.ParseBool((string)element.Attribute("CreativeFlying"), false),
            HasReceivedInitialItems = PlayerRecordCodec.ParseBool((string)element.Attribute("InitialItems"), true),
            InventoryWasCreative = PlayerRecordCodec.ParseBool((string)element.Attribute("CreativeInventory"), false),
            ActiveSlotIndex = (int?)element.Attribute("ActiveSlot") ?? 0,
            CreativeCategoryIndex = (int?)element.Attribute("CreativeCategory") ?? 0,
            CreativePageIndex = (int?)element.Attribute("CreativePage") ?? 0
            };
        }

        /// <summary>
        /// 读记录里的自定义皮肤资源（v7 追加的 &lt;SkinAsset&gt; 子元素）。
        /// 只做 base64 有界解码；合法性（大小 / PNG / 哈希）由 EnsureLoaded 校验并明确记日志，
        /// 损坏的资源不会被当成"可广播的资源"。
        /// </summary>
        private static byte[] ReadSkinAsset(XElement element, out string reason)
        {
            reason = null;
            if (element == null) return Array.Empty<byte>();
            string text = element.Value;
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte>();
            text = text.Trim();
            // base64 上限：256 KiB 原始字节约 350 KiB 文本；超过一定不是合法资产。
            if (text.Length > (SkinAssetPolicy.MaxAssetBytes / 3 + 1) * 4 + 8)
            {
                reason = "记录里的皮肤资源文本过长（" + text.Length + " 字符）";
                return Array.Empty<byte>();
            }
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException e)
            {
                reason = "记录里的皮肤资源不是合法 base64：" + e.Message;
                return Array.Empty<byte>();
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:201-214
        private static void ReadInventory(XElement inventory,
            out int[] slotValues, out int[] slotCounts)
        {
            XElement[] slots = inventory?.Elements("Slot").OrderBy(slot =>
                (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
            int slotsCount = slots.Length == 0 ? 0 : slots.Max(slot =>
                (int?)slot.Attribute("Index") ?? 0) + 1;
            slotValues = new int[slotsCount];
            slotCounts = new int[slotsCount];
            foreach (XElement slot in slots)
            {
                int index = (int?)slot.Attribute("Index") ?? -1;
                if (index < 0 || index >= slotsCount) continue;
                slotValues[index] = (int?)slot.Attribute("Value") ?? 0;
                slotCounts[index] = (int?)slot.Attribute("Count") ?? 0;
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:215-230
        private static void ReadHandcrafting(XElement handcrafting, NetworkPlayerRecord record)
        {
            XElement[] slots = handcrafting?.Elements("Slot").OrderBy(slot =>
                (int?)slot.Attribute("Index") ?? 0).ToArray() ?? Array.Empty<XElement>();
            int slotsCount = slots.Length == 0 ? 0 : slots.Max(slot =>
                (int?)slot.Attribute("Index") ?? 0) + 1;
            record.HandcraftSlotValues = new int[slotsCount];
            record.HandcraftSlotCounts = new int[slotsCount];
            foreach (XElement slot in slots)
            {
                int index = (int?)slot.Attribute("Index") ?? -1;
                if (index < 0 || index >= slotsCount) continue;
                record.HandcraftSlotValues[index] = (int?)slot.Attribute("Value") ?? 0;
                record.HandcraftSlotCounts[index] = (int?)slot.Attribute("Count") ?? 0;
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:231-239
        private static void ReadSatiation(XElement satiation, NetworkPlayerRecord record)
        {
            record.Satiation = new Dictionary<int, float>();
            foreach (XElement item in satiation?.Elements("Item") ?? Enumerable.Empty<XElement>())
            {
                int value = (int?)item.Attribute("Value") ?? 0;
                float remaining = PlayerRecordCodec.ParseFloat((string)item.Attribute("Remaining"));
                if (value != 0 && remaining > 0f) record.Satiation[value] = remaining;
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:248-255
        private static void ReadClothes(XElement clothes, NetworkPlayerRecord record)
        {
            record.Clothes = PlayerRecordCodec.CreateEmptyClothes();
            foreach (XElement slot in clothes?.Elements("Slot") ?? Enumerable.Empty<XElement>())
            {
                int index = (int?)slot.Attribute("Index") ?? -1;
                if (index >= 0 && index < record.Clothes.Length)
                    record.Clothes[index] = PlayerRecordCodec.ParseIntArray((string)slot.Attribute("Values"));
            }
        }

        // Source: ScMultiplayerProfileHandlers.cs:283-361
        private static XElement WritePlayer(string recordKey, NetworkPlayerRecord record)
        {
            var player = new XElement("Player",
                new XAttribute("Identity", recordKey),
                // 每角色独立 hash（v9）：跟着记录走（世界 pak 里也有这份文件），不写进 Project.xml。
                new XAttribute("RecordHash", PlayerRecordHash.Format(record.RecordHash)),
                new XAttribute("Name", record.Name ?? "Player"),
                new XAttribute("NameConfirmed", record.NameConfirmed),
                new XAttribute("Class", record.PlayerClass),
                new XAttribute("Skin", record.SkinName ?? string.Empty),
                new XAttribute("SkinSha256", PlayerRecordCodec.FormatSkinHash(record.SkinSha256)),
                new XAttribute("X", PlayerRecordCodec.FormatFloat(record.Position.X)),
                new XAttribute("Y", PlayerRecordCodec.FormatFloat(record.Position.Y)),
                new XAttribute("Z", PlayerRecordCodec.FormatFloat(record.Position.Z)),
                new XAttribute("SpawnX", PlayerRecordCodec.FormatFloat(record.SpawnPosition.X)),
                new XAttribute("SpawnY", PlayerRecordCodec.FormatFloat(record.SpawnPosition.Y)),
                new XAttribute("SpawnZ", PlayerRecordCodec.FormatFloat(record.SpawnPosition.Z)),
                new XAttribute("Level", PlayerRecordCodec.FormatFloat(record.Level)),
                new XAttribute("Health", PlayerRecordCodec.FormatFloat(record.Health)),
                new XAttribute("Air", PlayerRecordCodec.FormatFloat(record.Air)),
                new XAttribute("Food", PlayerRecordCodec.FormatFloat(record.Food)),
                new XAttribute("Stamina", PlayerRecordCodec.FormatFloat(record.Stamina)),
                new XAttribute("Sleep", PlayerRecordCodec.FormatFloat(record.Sleep)),
                new XAttribute("Temperature", PlayerRecordCodec.FormatFloat(record.Temperature)),
                new XAttribute("TargetTemperature", PlayerRecordCodec.FormatFloat(record.TargetTemperature)),
                new XAttribute("Wetness", PlayerRecordCodec.FormatFloat(record.Wetness)),
                new XAttribute("FluDuration", PlayerRecordCodec.FormatFloat(record.FluDuration)),
                new XAttribute("FluOnset", PlayerRecordCodec.FormatFloat(record.FluOnset)),
                new XAttribute("SicknessDuration", PlayerRecordCodec.FormatFloat(record.SicknessDuration)),
                new XAttribute("BodyQX", PlayerRecordCodec.FormatFloat(record.BodyRotation.X)),
                new XAttribute("BodyQY", PlayerRecordCodec.FormatFloat(record.BodyRotation.Y)),
                new XAttribute("BodyQZ", PlayerRecordCodec.FormatFloat(record.BodyRotation.Z)),
                new XAttribute("BodyQW", PlayerRecordCodec.FormatFloat(record.BodyRotation.W)),
                new XAttribute("LookX", PlayerRecordCodec.FormatFloat(record.LookAngles.X)),
                new XAttribute("LookY", PlayerRecordCodec.FormatFloat(record.LookAngles.Y)),
                new XAttribute("FireDuration", PlayerRecordCodec.FormatFloat(record.FireDuration)),
                new XAttribute("CreativeFlying", record.IsCreativeFlying),
                new XAttribute("InitialItems", record.HasReceivedInitialItems),
                new XAttribute("CreativeInventory", record.InventoryWasCreative),
                new XAttribute("ActiveSlot", record.ActiveSlotIndex),
                new XAttribute("CreativeCategory", record.CreativeCategoryIndex),
                new XAttribute("CreativePage", record.CreativePageIndex));
            // 自定义皮肤资源（世界作用域）：主机记录是重连 / 新客户端补发的唯一来源。
            if (record.SkinAsset != null && record.SkinAsset.Length > 0 &&
                PlayerRecordCodec.IsSkinHashValid(record.SkinSha256))
            {
                player.Add(new XElement("SkinAsset",
                    new XAttribute("Sha256", PlayerRecordCodec.FormatSkinHash(record.SkinSha256)),
                    new XAttribute("Length", record.SkinAsset.Length),
                    Convert.ToBase64String(record.SkinAsset)));
            }
            var inventory = new XElement("Inventory");
            int slotsCount = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
            for (int i = 0; i < slotsCount; i++)
            {
                inventory.Add(new XElement("Slot", new XAttribute("Index", i),
                    new XAttribute("Value", record.SlotValues[i]),
                    new XAttribute("Count", record.SlotCounts[i])));
            }
            player.Add(inventory);
            var handcrafting = new XElement("Handcrafting");
            int handcraftSlotsCount = Math.Min(
                record.HandcraftSlotValues?.Length ?? 0, record.HandcraftSlotCounts?.Length ?? 0);
            for (int i = 0; i < handcraftSlotsCount; i++)
            {
                if (!PlayerRecordCodec.ShouldPersistItem(
                    record.HandcraftSlotValues[i], record.HandcraftSlotCounts[i]))
                    continue;
                handcrafting.Add(new XElement("Slot", new XAttribute("Index", i),
                    new XAttribute("Value", record.HandcraftSlotValues[i]),
                    new XAttribute("Count", record.HandcraftSlotCounts[i])));
            }
            player.Add(handcrafting);
            var satiation = new XElement("Satiation");
            foreach (KeyValuePair<int, float> item in record.Satiation ?? new Dictionary<int, float>())
            {
                if (item.Key == 0 || item.Value <= 0f) continue;
                satiation.Add(new XElement("Item",
                    new XAttribute("Value", item.Key),
                    new XAttribute("Remaining", PlayerRecordCodec.FormatFloat(item.Value))));
            }
            player.Add(satiation);
            var clothes = new XElement("Clothes");
            int[][] clothesValues = record.Clothes ?? PlayerRecordCodec.CreateEmptyClothes();
            for (int i = 0; i < 4; i++)
            {
                clothes.Add(new XElement("Slot", new XAttribute("Index", i),
                    new XAttribute("Values", PlayerRecordCodec.FormatIntArray(
                        i < clothesValues.Length ? clothesValues[i] : null))));
            }
            player.Add(clothes);
            // 游戏统计：由主机从权威 PlayerStats 抓取、存在角色记录里（不写 Project.xml）
            if (record.Stats != null && !record.Stats.IsEmpty) player.Add(record.Stats.ToXml());
            // 成就账本（v8）：主机权威，id + UTC 解锁时间；未知 id 也原样保留。
            if (record.Achievements != null && !record.Achievements.IsEmpty)
                player.Add(record.Achievements.ToXml());
            return player;
        }
    }
}

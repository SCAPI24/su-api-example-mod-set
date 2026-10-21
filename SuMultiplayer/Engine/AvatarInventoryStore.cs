using System;
using Engine;
using SuMultiplayer.Modules;
using SuMultiplayer.Net;
using SuMultiplayer.Players;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 主机侧背包落盘（<see cref="IPlayerInventoryStore"/> 的实现）：
    /// 把**客户端上报并被主机接受**的可用背包写进角色记录
    /// <c>&lt;世界目录&gt;/SuMultiplayerPlayers.xml</c>，重连 / 加入时由
    /// <see cref="TryLoadInventory"/> 读回做关键帧 —— 这就是"背包随角色持久化"的唯一落点。
    ///
    /// 旧实现对应处：
    ///   Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:576-618
    ///           （RestorePlayerRecordInventory：记录槽位 → 角色背包）
    ///   Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerProfileHandlers.cs:376-467
    ///           （CapturePlayerRecord：角色背包 → 记录槽位）
    ///   Source: Mod/ScMultiplayer/Modules/Player/ScMultiplayerPlayerSync.cs:484-488
    ///           （Equipment 消息落地后 m_playerRecords[recordKey] = CapturePlayerRecord(player); dirty = true）
    ///
    /// 两个刻意的边界（都在报告里写明）：
    ///   · 只覆盖**同步范围**（<see cref="PlayerInventoryMath.SyncSlotsFor"/>）：普通背包（26 槽）
    ///     全部槽位；创造仓库（1622 槽）仍只覆盖前 <see cref="PlayerInventoryMath.MaxSyncSlots"/> 槽，
    ///     记录里超出的槽位保持原样，由主机 5 秒周期的角色记录抓取照常更新 —— 不在这里截断记录。
    ///   · 写盘按 1 秒节流：背包每次变化都重写 XML 会造成磁盘抖动；离开 / 5 秒周期仍会落盘，
    ///     所以最坏情况丢的是最近 1 秒内的变化，而不是整份背包。
    /// </summary>
    public sealed class AvatarInventoryStore : IPlayerInventoryStore
    {
        private const long SaveIntervalMs = 1000;

        private readonly NetworkAvatarSpawner m_spawner;
        private readonly SuMultiplayerPlayersXml m_records;
        private readonly IModuleDiagnostics m_diagnostics;
        private readonly string m_ownerId;
        private readonly System.Collections.Generic.HashSet<string> m_loggedOnce =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private long m_lastSaveMs;
        private long m_stored;
        private long m_saves;
        private long m_saveFailures;
        private long m_loaded;

        public AvatarInventoryStore(NetworkAvatarSpawner spawner, SuMultiplayerPlayersXml records,
            IModuleDiagnostics diagnostics, string ownerId)
        {
            m_spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            m_records = records ?? throw new ArgumentNullException(nameof(records));
            m_diagnostics = diagnostics;
            m_ownerId = string.IsNullOrEmpty(ownerId) ? "player-inventory" : ownerId;
        }

        public bool StoreInventory(int clientId, int[] values, int[] counts, int activeSlotIndex,
            int inventoryVersion, out string reason)
        {
            reason = null;
            if (values == null || counts == null || values.Length != counts.Length)
            {
                reason = "背包数组为空或长度不一致";
                return false;
            }
            if (!m_spawner.TryGetRecordKey(clientId, out string recordKey) ||
                string.IsNullOrEmpty(recordKey))
            {
                reason = "clientId=" + clientId + " 还没有角色记录键（联机角色没建好）";
                return false;
            }
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record) || record == null)
            {
                reason = "身份 " + PlayerIdentity.Describe(recordKey) + " 没有角色记录";
                return false;
            }
            // values.Length 就是抓取侧的同步范围（普通 26 / 创造 10），由 SyncSlotsFor 决定
            // （它保证返回值 ≤ 输入，不需要再套一层 Math.Min）。
            int slots = PlayerInventoryMath.SyncSlotsFor(values.Length);
            if (slots <= 0)
            {
                reason = "背包数组长度为 " + values.Length + "，同步槽位数为 0（没有可落盘的槽位）";
                return false;
            }
            if (record.SlotValues == null || record.SlotCounts == null ||
                record.SlotValues.Length < slots || record.SlotCounts.Length < slots)
            {
                record.SlotValues = new int[slots];
                record.SlotCounts = new int[slots];
            }
            Array.Copy(values, record.SlotValues, slots);
            Array.Copy(counts, record.SlotCounts, slots);
            record.ActiveSlotIndex = Math.Max(activeSlotIndex, 0);
            m_records.Put(recordKey, record);
            m_stored++;
            long nowMs = (long)(Time.RealTime * 1000.0);
            if (nowMs - m_lastSaveMs >= SaveIntervalMs)
            {
                m_lastSaveMs = nowMs;
                if (m_records.SaveIfDirty()) m_saves++;
                else m_saveFailures++;
            }
            LogOnce("store:" + recordKey,
                "客户端背包已写进角色记录：身份 " + PlayerIdentity.Describe(recordKey) +
                " · " + slots + " 槽（" + (slots > PlayerInventoryMath.MaxSyncSlots
                    ? "普通背包全量" : "创造仓库前 " + PlayerInventoryMath.MaxSyncSlots + " 槽") +
                "）· 活跃槽 " + record.ActiveSlotIndex +
                "（写盘按 1 秒节流；离开 / 5 秒周期仍会落盘）");
            return true;
        }

        public bool TryLoadInventory(string identity, string name, out int[] values,
            out int[] counts, out int activeSlotIndex, out string reason)
        {
            values = null;
            counts = null;
            activeSlotIndex = -1;
            reason = null;
            string recordKey = PlayerIdentity.RecordKey(identity, name);
            if (!m_records.TryGet(recordKey, out NetworkPlayerRecord record) || record == null)
            {
                reason = "身份 " + PlayerIdentity.Describe(recordKey) + " 没有角色记录";
                return false;
            }
            int slots = Math.Min(record.SlotValues?.Length ?? 0, record.SlotCounts?.Length ?? 0);
            if (slots <= 0)
            {
                reason = "身份 " + PlayerIdentity.Describe(recordKey) + " 的角色记录里没有背包槽位";
                return false;
            }
            // 记录里可能是普通背包 26 槽或创造仓库记录（只有前 10 槽参与同步）：
            // 按记录自己的类型决定关键帧长度。Source: PlayerRecordCodec.LooksLikeLegacyCreativeInventory
            int syncSlots = record.InventoryWasCreative
                ? Math.Min(slots, PlayerInventoryMath.MaxSyncSlots)
                : PlayerInventoryMath.SyncSlotsFor(slots);
            slots = syncSlots;
            values = new int[slots];
            counts = new int[slots];
            Array.Copy(record.SlotValues, values, slots);
            Array.Copy(record.SlotCounts, counts, slots);
            activeSlotIndex = MathUtils.Clamp(record.ActiveSlotIndex, 0, slots - 1);
            m_loaded++;
            LogOnce("load:" + recordKey,
                "读回角色记录里的背包：身份 " + PlayerIdentity.Describe(recordKey) + " · " + slots +
                " 槽（" + (record.InventoryWasCreative
                    ? "创造仓库前 " + PlayerInventoryMath.MaxSyncSlots + " 槽" : "普通背包全量") +
                "）· 活跃槽 " + activeSlotIndex + "（重连 / 加入关键帧）");
            return true;
        }

        /// <summary>组合根周期打印的一行（落盘次数 / 读回次数 / 文件状态）。</summary>
        public string StatusLine()
        {
            return "player-inventory: " + Describe();
        }

        public string Describe()
        {
            return "inventory-store(stored=" + m_stored + " saves=" + m_saves +
                " saveFailures=" + m_saveFailures + " loaded=" + m_loaded +
                " loadFailed=" + (m_records.LoadFailed ? "yes" : "no") +
                " world=" + (m_records.WorldDirectory.Length > 0 ? "yes" : "no") + ")";
        }

        private void LogOnce(string key, string message)
        {
            if (!m_loggedOnce.Add(key)) return;
            m_diagnostics?.Info(m_ownerId, message);
        }
    }
}

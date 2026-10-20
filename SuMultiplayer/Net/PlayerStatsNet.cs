using System;
using System.Collections.Generic;
using SuMultiplayer.Players;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>解码失败分类：决定模块该加哪个计数器（stats / achv / 结构）。</summary>
    public enum PlayerStatsWireError
    {
        None = 0,
        /// <summary>结构非法（chunk_count / chunk_index / identity / 条数超上限 / revision）。</summary>
        Structure = 1,
        /// <summary>统计条目非法（名字 / 值 / 重复）。</summary>
        StatsMalformed = 2,
        /// <summary>成就条目非法（id / 时间戳 / 重复）。</summary>
        AchievementMalformed = 3
    }

    /// <summary>一条解码后的统计消息分片（快照或增量）。</summary>
    public sealed class PlayerStatsChunk
    {
        public int ClientId;
        public string Identity = string.Empty;
        public string PlayerName = string.Empty;
        public uint Generation;
        public ulong Revision;
        public uint ChunkIndex;
        public uint ChunkCount;
        public bool IsSnapshot;
        public readonly PlayerStatSet Stats = new PlayerStatSet();
        public readonly List<AchievementUnlock> Achievements = new List<AchievementUnlock>();

        public bool IsLastChunk => ChunkCount > 0 && ChunkIndex + 1 == ChunkCount;

        public string Describe() =>
            (IsSnapshot ? "snapshot" : "delta") + " id=" + Identity +
            " chunk=" + (ChunkIndex + 1) + "/" + ChunkCount +
            " stats=" + Stats.Count + " achv=" + Achievements.Count +
            (IsSnapshot ? " generation=" + Generation : " revision=" + Revision);
    }

    /// <summary>
    /// 玩家统计 / 成就的**唯一线格式编解码**（纯逻辑，不依赖 Game）。
    ///
    /// 为什么分片：一份完整统计 ≈ 35 个 [Stat] 字段（名字 + 值），成就也会增长。
    /// 单条消息最多 <see cref="MaxStatsPerChunk"/> 条统计 + <see cref="MaxAchievementsPerChunk"/>
    /// 条成就，最多 <see cref="MaxChunkCount"/> 片；每片远小于帧上限 60 KiB
    /// （<c>TerrainNet.TerrainFrame.MaxPayloadBytes</c>）。超限在编码时**明确失败**，
    /// 解码时整条拒绝（绝不截断后假装成功）。
    ///
    /// 分片规则：chunk_count = max(1, ceil(statCount/24), ceil(achvCount/24))；
    /// 第 i 片携带统计的 [i*24, i*24+24) 与成就的 [i*24, i*24+24)（越界则为空）。
    /// </summary>
    public static class PlayerStatsWire
    {
        /// <summary>单条消息最多几条统计。</summary>
        public const int MaxStatsPerChunk = 24;

        /// <summary>单条消息最多几条成就。</summary>
        public const int MaxAchievementsPerChunk = 24;

        /// <summary>一份快照 / 一条增量最多几片（96/24=4 与 200/24≈9 都在内）。</summary>
        public const int MaxChunkCount = 16;

        /// <summary>identity 上限（user: + GUID = 41；自定义身份也留足余量）。</summary>
        public const int MaxIdentityLength = 96;

        /// <summary>展示名上限（引擎 PlayerData.VerifyName 限 14；留足余量）。</summary>
        public const int MaxPlayerNameLength = 64;

        /// <summary>单条消息硬上限（保守值，远低于帧上限 60 KiB）。</summary>
        public const int MaxPayloadBytes = 16 * 1024;

        /// <summary>分片数（至少 1；调用方保证条目数在 <see cref="PlayerStatSet.MaxEntries"/> 内）。</summary>
        public static int ChunkCountFor(int statCount, int achievementCount)
        {
            int stats = statCount <= 0 ? 0 : (statCount + MaxStatsPerChunk - 1) / MaxStatsPerChunk;
            int achievements = achievementCount <= 0
                ? 0
                : (achievementCount + MaxAchievementsPerChunk - 1) / MaxAchievementsPerChunk;
            return Math.Max(1, Math.Max(stats, achievements));
        }

        // ---------------------------------------------------------------- 编码

        public static bool TryEncodeSnapshot(int clientId, string identity, string playerName,
            uint generation, PlayerStatSet stats, IReadOnlyList<AchievementUnlock> achievements,
            out List<P.PlayerStatsSnapshot> messages, out string reason)
        {
            messages = null;
            if (!ValidateHeader(clientId, identity, playerName, out reason)) return false;
            if (!ValidateEntries(stats, achievements, out reason)) return false;
            List<KeyValuePair<string, string>> statEntries = Sorted(stats);
            List<AchievementUnlock> unlocks = SortedAchievements(achievements);
            int chunkCount = ChunkCountFor(statEntries.Count, unlocks.Count);
            if (chunkCount > MaxChunkCount)
            {
                reason = "快照需要 " + chunkCount + " 片 > 上限 " + MaxChunkCount;
                return false;
            }
            var list = new List<P.PlayerStatsSnapshot>(chunkCount);
            for (int index = 0; index < chunkCount; index++)
            {
                var message = new P.PlayerStatsSnapshot
                {
                    ClientId = clientId,
                    Identity = identity,
                    PlayerName = playerName ?? string.Empty,
                    Generation = generation,
                    ChunkIndex = (uint)index,
                    ChunkCount = (uint)chunkCount
                };
                AddStats(message.Stats, statEntries, index * MaxStatsPerChunk);
                AddAchievements(message.Achievements, unlocks, index * MaxAchievementsPerChunk);
                if (message.CalculateSize() > MaxPayloadBytes)
                {
                    reason = "统计快照分片超过单条上限 " + MaxPayloadBytes + " 字节";
                    return false;
                }
                list.Add(message);
            }
            messages = list;
            return true;
        }

        public static bool TryEncodeDelta(int clientId, string identity, string playerName,
            uint generation, ulong revision,
            IReadOnlyList<KeyValuePair<string, string>> changedStats,
            IReadOnlyList<AchievementUnlock> newAchievements,
            out List<P.PlayerStatsDelta> messages, out string reason)
        {
            messages = null;
            if (!ValidateHeader(clientId, identity, playerName, out reason)) return false;
            if (revision == 0)
            {
                reason = "增量 revision 必须 ≥ 1（0 保留给\"没有增量\"）";
                return false;
            }
            var statEntries = new List<KeyValuePair<string, string>>(
                changedStats ?? Array.Empty<KeyValuePair<string, string>>());
            var unlocks = new List<AchievementUnlock>(newAchievements ?? Array.Empty<AchievementUnlock>());
            if (statEntries.Count > PlayerStatSet.MaxEntries)
            {
                reason = "增量统计条目 " + statEntries.Count + " > 上限 " + PlayerStatSet.MaxEntries;
                return false;
            }
            if (unlocks.Count > AchievementLedger.MaxAchievements)
            {
                reason = "增量成就条目 " + unlocks.Count + " > 上限 " + AchievementLedger.MaxAchievements;
                return false;
            }
            foreach (KeyValuePair<string, string> item in statEntries)
            {
                if (!PlayerStatSet.IsValidName(item.Key, out reason)) return false;
                if (!PlayerStatSet.IsValidValue(item.Value, out reason)) return false;
            }
            foreach (AchievementUnlock unlock in unlocks)
            {
                if (!AchievementLedger.IsValidId(unlock.Id, out reason)) return false;
                if (unlock.UnlockedUtcSeconds < 0)
                {
                    reason = "成就时间戳为负（" + unlock.UnlockedUtcSeconds + "）";
                    return false;
                }
            }
            statEntries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            unlocks.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            int chunkCount = ChunkCountFor(statEntries.Count, unlocks.Count);
            if (chunkCount > MaxChunkCount)
            {
                reason = "增量需要 " + chunkCount + " 片 > 上限 " + MaxChunkCount;
                return false;
            }
            var list = new List<P.PlayerStatsDelta>(chunkCount);
            for (int index = 0; index < chunkCount; index++)
            {
                var message = new P.PlayerStatsDelta
                {
                    ClientId = clientId,
                    Identity = identity,
                    PlayerName = playerName ?? string.Empty,
                    Generation = generation,
                    Revision = revision,
                    ChunkIndex = (uint)index,
                    ChunkCount = (uint)chunkCount
                };
                AddStats(message.Stats, statEntries, index * MaxStatsPerChunk);
                AddAchievements(message.Achievements, unlocks, index * MaxAchievementsPerChunk);
                if (message.CalculateSize() > MaxPayloadBytes)
                {
                    reason = "统计增量分片超过单条上限 " + MaxPayloadBytes + " 字节";
                    return false;
                }
                list.Add(message);
            }
            messages = list;
            return true;
        }

        // ---------------------------------------------------------------- 解码

        public static bool TryDecodeSnapshot(P.PlayerStatsSnapshot message,
            out PlayerStatsChunk chunk, out PlayerStatsWireError error, out string reason)
        {
            chunk = null;
            error = PlayerStatsWireError.Structure;
            if (message == null)
            {
                reason = "消息为空";
                return false;
            }
            if (!ValidateChunkHeader(message.ClientId, message.Identity, message.PlayerName,
                    message.ChunkIndex, message.ChunkCount, message.Stats.Count,
                    message.Achievements.Count, out reason))
                return false;
            var result = new PlayerStatsChunk
            {
                ClientId = message.ClientId,
                Identity = message.Identity,
                PlayerName = message.PlayerName ?? string.Empty,
                Generation = message.Generation,
                Revision = 0,
                ChunkIndex = message.ChunkIndex,
                ChunkCount = message.ChunkCount,
                IsSnapshot = true
            };
            if (!DecodeStats(message.Stats, result, out error, out reason)) return false;
            if (!DecodeAchievements(message.Achievements, result, out error, out reason)) return false;
            error = PlayerStatsWireError.None;
            chunk = result;
            return true;
        }

        public static bool TryDecodeDelta(P.PlayerStatsDelta message,
            out PlayerStatsChunk chunk, out PlayerStatsWireError error, out string reason)
        {
            chunk = null;
            error = PlayerStatsWireError.Structure;
            if (message == null)
            {
                reason = "消息为空";
                return false;
            }
            if (!ValidateChunkHeader(message.ClientId, message.Identity, message.PlayerName,
                    message.ChunkIndex, message.ChunkCount, message.Stats.Count,
                    message.Achievements.Count, out reason))
                return false;
            if (message.Revision == 0)
            {
                reason = "增量 revision 为 0（保留值）";
                return false;
            }
            var result = new PlayerStatsChunk
            {
                ClientId = message.ClientId,
                Identity = message.Identity,
                PlayerName = message.PlayerName ?? string.Empty,
                Generation = message.Generation,
                Revision = message.Revision,
                ChunkIndex = message.ChunkIndex,
                ChunkCount = message.ChunkCount,
                IsSnapshot = false
            };
            if (!DecodeStats(message.Stats, result, out error, out reason)) return false;
            if (!DecodeAchievements(message.Achievements, result, out error, out reason)) return false;
            error = PlayerStatsWireError.None;
            chunk = result;
            return true;
        }

        // ---------------------------------------------------------------- 校验 / 工具

        private static bool ValidateHeader(int clientId, string identity, string playerName,
            out string reason)
        {
            reason = null;
            if (clientId < 0)
            {
                reason = "client_id 为负（" + clientId + "）";
                return false;
            }
            if (string.IsNullOrEmpty(identity) || identity.Length > MaxIdentityLength)
            {
                reason = "identity 为空或过长（" + (identity?.Length ?? 0) + "）";
                return false;
            }
            if (playerName != null && playerName.Length > MaxPlayerNameLength)
            {
                reason = "player_name 过长（" + playerName.Length + "）";
                return false;
            }
            return true;
        }

        private static bool ValidateChunkHeader(int clientId, string identity, string playerName,
            uint chunkIndex, uint chunkCount, int statCount, int achievementCount,
            out string reason)
        {
            if (!ValidateHeader(clientId, identity, playerName, out reason)) return false;
            if (chunkCount == 0 || chunkCount > MaxChunkCount)
            {
                reason = "chunk_count 非法（" + chunkCount + "，允许 1.." + MaxChunkCount + "）";
                return false;
            }
            if (chunkIndex >= chunkCount)
            {
                reason = "chunk_index " + chunkIndex + " ≥ chunk_count " + chunkCount;
                return false;
            }
            if (statCount > MaxStatsPerChunk)
            {
                reason = "单条统计条目 " + statCount + " > 上限 " + MaxStatsPerChunk;
                return false;
            }
            if (achievementCount > MaxAchievementsPerChunk)
            {
                reason = "单条成就条目 " + achievementCount + " > 上限 " + MaxAchievementsPerChunk;
                return false;
            }
            return true;
        }

        private static bool ValidateEntries(PlayerStatSet stats,
            IReadOnlyList<AchievementUnlock> achievements, out string reason)
        {
            reason = null;
            if (stats != null && stats.Count > PlayerStatSet.MaxEntries)
            {
                reason = "统计条目 " + stats.Count + " > 上限 " + PlayerStatSet.MaxEntries;
                return false;
            }
            if (achievements != null && achievements.Count > AchievementLedger.MaxAchievements)
            {
                reason = "成就条目 " + achievements.Count + " > 上限 " +
                    AchievementLedger.MaxAchievements;
                return false;
            }
            return true;
        }

        private static bool DecodeStats(IEnumerable<P.PlayerStatEntry> entries,
            PlayerStatsChunk target, out PlayerStatsWireError error, out string reason)
        {
            error = PlayerStatsWireError.None;
            reason = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (P.PlayerStatEntry entry in entries)
            {
                if (entry == null) continue;
                if (!seen.Add(entry.Name))
                {
                    error = PlayerStatsWireError.StatsMalformed;
                    reason = "同一条消息里统计名字重复：" + (entry.Name ?? "(null)");
                    return false;
                }
                if (!target.Stats.TrySet(entry.Name, entry.Value, out reason))
                {
                    error = PlayerStatsWireError.StatsMalformed;
                    return false;
                }
            }
            return true;
        }

        private static bool DecodeAchievements(IEnumerable<P.PlayerAchievementEntry> entries,
            PlayerStatsChunk target, out PlayerStatsWireError error, out string reason)
        {
            error = PlayerStatsWireError.None;
            reason = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (P.PlayerAchievementEntry entry in entries)
            {
                if (entry == null) continue;
                if (!seen.Add(entry.Id))
                {
                    error = PlayerStatsWireError.AchievementMalformed;
                    reason = "同一条消息里成就 id 重复：" + (entry.Id ?? "(null)");
                    return false;
                }
                if (!AchievementLedger.IsValidId(entry.Id, out reason))
                {
                    error = PlayerStatsWireError.AchievementMalformed;
                    return false;
                }
                if (entry.UnlockedUtcSeconds < 0)
                {
                    error = PlayerStatsWireError.AchievementMalformed;
                    reason = "成就时间戳为负（" + entry.UnlockedUtcSeconds + "）";
                    return false;
                }
                target.Achievements.Add(new AchievementUnlock(entry.Id, entry.UnlockedUtcSeconds));
            }
            return true;
        }

        private static List<KeyValuePair<string, string>> Sorted(PlayerStatSet stats)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (stats != null) list.AddRange(stats.Entries);
            return list;
        }

        private static List<AchievementUnlock> SortedAchievements(
            IReadOnlyList<AchievementUnlock> achievements)
        {
            var list = new List<AchievementUnlock>(
                achievements ?? Array.Empty<AchievementUnlock>());
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        private static void AddStats(Google.Protobuf.Collections.RepeatedField<P.PlayerStatEntry> field,
            List<KeyValuePair<string, string>> entries, int start)
        {
            int end = Math.Min(entries.Count, start + MaxStatsPerChunk);
            for (int index = start; index < end; index++)
            {
                field.Add(new P.PlayerStatEntry
                {
                    Name = entries[index].Key,
                    Value = entries[index].Value ?? string.Empty
                });
            }
        }

        private static void AddAchievements(
            Google.Protobuf.Collections.RepeatedField<P.PlayerAchievementEntry> field,
            List<AchievementUnlock> unlocks, int start)
        {
            int end = Math.Min(unlocks.Count, start + MaxAchievementsPerChunk);
            for (int index = start; index < end; index++)
            {
                field.Add(new P.PlayerAchievementEntry
                {
                    Id = unlocks[index].Id,
                    UnlockedUtcSeconds = unlocks[index].UnlockedUtcSeconds
                });
            }
        }
    }
}

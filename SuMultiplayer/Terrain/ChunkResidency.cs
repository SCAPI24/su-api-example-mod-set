using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// 一块区块的**内容生成阶段**（纯托管口径，不引用 <c>Game.TerrainChunkState</c>，
    /// 因为本文件要和 <c>P:\tmp\tsverify</c> 一起编译做真实自检）。
    ///
    /// 与引擎枚举的对应（Survivalcraft/Game/TerrainChunkState.cs）：
    ///   NotLoaded(0)                          → <see cref="NotLoaded"/>
    ///   InvalidContents1..InvalidContents4    → <see cref="Contents"/>
    ///   InvalidLight(5) / InvalidPropagatedLight(6) / InvalidVertices1(7) /
    ///   InvalidVertices2(8) / Valid(9)        → <see cref="ContentValid"/>
    ///
    /// **内容可用的唯一判据是"State 超过 InvalidContents4"**（引擎序列化器
    /// <c>TerrainSerializer23.SaveChunk</c> / <c>TerrainSerializer22.SaveChunk</c> 用的同一句）。
    /// 特别地：**不能**再叠加 <c>TerrainChunk.IsLoaded</c> —— 那个标志只在
    /// <c>TerrainUpdater.UpdateChunkSingleStep</c> 里 <c>TerrainSerializer.LoadChunk</c> 成功时置位
    /// （TerrainUpdater.cs:735），而引擎**自己生成**的块从不写盘
    /// （<c>SaveChunk</c> 只在 <c>ModificationCounter &gt; 0</c> 时保存）⇒ 生成出来的块永远是
    /// <c>IsLoaded == false</c>。把它当"内容可用"会让"客户端自己生成 / 重载"的块永远进不了常驻集合
    /// （= 永不摘要、永不上报、主机永不给裁决 ⇒ 实时地图灰框）。2026-10-18 实机证据：
    /// 客户端常驻 93 / 已分配 201，而它脚下那块 (34,9) 有真实地形（Dirt/Grass + 天光 14）⇒ 内容可用。
    /// </summary>
    public enum ChunkContentStage : byte
    {
        /// <summary>引擎还没跑 LoadChunk 那一步（State = NotLoaded）。</summary>
        NotLoaded = 0,
        /// <summary>内容生成阶段（InvalidContents1..4）：格子在写，但还没定稿。</summary>
        Contents = 1,
        /// <summary>内容已定稿（InvalidLight 及以后，含 Valid）：可以算摘要 / 上报。</summary>
        ContentValid = 2
    }

    /// <summary>引擎 <c>TerrainChunkState</c> 序数 ↔ <see cref="ChunkContentStage"/> 的唯一换算（只有一份）。</summary>
    public static class ChunkContentStages
    {
        /// <summary>引擎 TerrainChunkState.NotLoaded 的序数。</summary>
        public const int EngineStateNotLoaded = 0;
        /// <summary>引擎 TerrainChunkState.InvalidContents4 的序数（内容阶段的最后一步）。</summary>
        public const int EngineStateInvalidContents4 = 4;
        /// <summary>引擎 TerrainChunkState.InvalidLight 的序数（内容已定稿的第一步）。</summary>
        public const int EngineStateInvalidLight = 5;
        /// <summary>引擎 TerrainChunkState.Valid 的序数。</summary>
        public const int EngineStateValid = 9;

        /// <summary>内容可用 = State 超过 InvalidContents4（与引擎序列化器同一判据；与 IsLoaded 无关）。</summary>
        public static bool IsContentValid(int engineState) => engineState > EngineStateInvalidContents4;

        /// <summary>把引擎 State 序数换成三段式阶段。</summary>
        public static ChunkContentStage OfEngineState(int engineState)
        {
            if (engineState <= EngineStateNotLoaded) return ChunkContentStage.NotLoaded;
            return IsContentValid(engineState) ? ChunkContentStage.ContentValid : ChunkContentStage.Contents;
        }

        /// <summary>日志用名字（与引擎枚举一致；未知值照实写数字，不猜）。</summary>
        public static string DescribeEngineState(int engineState)
        {
            switch (engineState)
            {
                case 0: return "NotLoaded";
                case 1: return "InvalidContents1";
                case 2: return "InvalidContents2";
                case 3: return "InvalidContents3";
                case 4: return "InvalidContents4";
                case 5: return "InvalidLight";
                case 6: return "InvalidPropagatedLight";
                case 7: return "InvalidVertices1";
                case 8: return "InvalidVertices2";
                case 9: return "Valid";
                default: return "State" + engineState.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// "这块已经在内存里、但内容一直没定稿"的**明确原因**（按原因分开计数，绝不合成一个糊涂数）。
    /// </summary>
    public enum ChunkStallReason : byte
    {
        None = 0,
        /// <summary>State = NotLoaded：引擎连 LoadChunk 这一步都还没轮到它。</summary>
        Serializer = 1,
        /// <summary>State = InvalidContents1..4：内容生成在队列里 / 正在写。</summary>
        Contents = 2,
        /// <summary>没有任何地形更新位置覆盖它（引擎永远不会推进它 —— 真正的"无法定稿"）。</summary>
        NoUpdateLocation = 3,
        /// <summary>读不到 TerrainUpdater 的更新位置（"无法定稿"与否无法判定；已单独告警）。</summary>
        LocationsUnknown = 4
    }

    public static class ChunkStallReasonNames
    {
        private static readonly string[] s_names =
        {
            "none", "serializer", "contents", "no-update-location", "locations-unknown"
        };

        public static string Of(ChunkStallReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < s_names.Length ? s_names[index] : "none";
        }

        public static int Count => s_names.Length;
    }

    /// <summary>本帧由 <see cref="ChunkResidencyTracker"/> 产生的状态变化（引擎桥据此发事件）。</summary>
    public enum ChunkResidencyEvent : byte
    {
        None = 0,
        /// <summary>进入内存（= 实时地图"内存区块集合"的 add）。</summary>
        Allocated = 1,
        /// <summary>离开内存（= 内存区块集合的 remove）。</summary>
        Freed = 2,
        /// <summary>内容定稿（= 该算摘要 / 上报的唯一信号）。</summary>
        BecameResident = 3,
        /// <summary>内容掉回未定稿（块还在内存里）。</summary>
        BecameNotResident = 4,
        /// <summary>超过宽限仍未定稿 → **明确不再作为"内存区块"上报**（原因见 Reason）。</summary>
        WireExcluded = 5,
        /// <summary>之前被排除的块后来定稿了 → 重新纳回上报集合（绝不静默恢复）。</summary>
        WireRestored = 6
    }

    /// <summary>一次状态变化（只读快照；键 + 事件 + 原因 + 阶段 + 已等待时长）。</summary>
    public readonly struct ChunkResidencyChange
    {
        public ChunkResidencyChange(ChunkKey key, ChunkResidencyEvent what, ChunkStallReason reason,
            ChunkContentStage stage, long ageMs)
        {
            Key = key;
            Event = what;
            Reason = reason;
            Stage = stage;
            AgeMs = ageMs;
        }

        public ChunkKey Key { get; }
        public ChunkResidencyEvent Event { get; }
        public ChunkStallReason Reason { get; }
        public ChunkContentStage Stage { get; }
        public long AgeMs { get; }
    }

    /// <summary>一块"超过宽限仍未定稿"的区块（桥用它写限流 Warning；调用方只读）。</summary>
    public readonly struct ChunkStallRecord
    {
        public ChunkStallRecord(ChunkKey key, ChunkStallReason reason, ChunkContentStage stage, long ageMs)
        {
            Key = key;
            Reason = reason;
            Stage = stage;
            AgeMs = ageMs;
        }

        public ChunkKey Key { get; }
        public ChunkStallReason Reason { get; }
        public ChunkContentStage Stage { get; }
        public long AgeMs { get; }
    }

    /// <summary>
    /// 区块"内存 / 常驻（内容可用）/ 未定稿 / 上报集合"的**唯一状态机**（纯托管、实例状态、无静态字段）。
    ///
    /// 三份事实（实时地图与摘要上报的口径必须分开，用户口径见 v8 协议）：
    ///   · **内存**（<see cref="MemoryCount"/>）：引擎已分配的块（= <c>terrain.AllocatedChunks</c>）；
    ///   · **常驻**（<see cref="ResidentCount"/>）：内容已定稿的块 —— 摘要 / 裁决 / 回传只认这一份；
    ///   · **上报集合**（<see cref="WireChunks"/>）：内存集合里**能被定稿**的那部分。正常就是 == 内存；
    ///     只有"超过 <see cref="GraceMs"/> 仍未定稿"的块会被明确排除（原因 + 计数 + 限流 Warning），
    ///     免得实时地图上永远挂着一块无法核对、也永远等不到裁决的灰框。
    ///
    /// 每帧用法（引擎桥 <c>TerrainBridge.Shot</c>，主线程）：
    /// <code>
    /// tracker.BeginFrame(nowMs);
    /// foreach (已分配块) tracker.Observe(key, stage, coveredByUpdateLocation);
    /// tracker.EndFrame();                      // 本帧没看到的块 = 已被引擎释放 → 产生事件
    /// foreach (change in tracker.Changes) 发引擎事件;
    /// </code>
    /// </summary>
    public sealed class ChunkResidencyTracker
    {
        /// <summary>默认宽限：块在内存里超过这么久还没定稿 → 明确排除出上报集合并告警（30 s）。</summary>
        public const int DefaultGraceMs = 30000;

        private struct NotReady
        {
            public long FirstMs;
            public ChunkStallReason Reason;
            public bool WireExcluded;
        }

        private readonly int m_graceMs;
        private readonly HashSet<ChunkKey> m_memory = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_resident = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_wire = new HashSet<ChunkKey>();
        private readonly HashSet<ChunkKey> m_seen = new HashSet<ChunkKey>();
        private readonly Dictionary<ChunkKey, NotReady> m_notReady =
            new Dictionary<ChunkKey, NotReady>();
        private readonly List<ChunkResidencyChange> m_changes = new List<ChunkResidencyChange>(512);
        private readonly List<ChunkStallRecord> m_newlyStalled = new List<ChunkStallRecord>(64);
        private readonly long[] m_stalledNow = new long[ChunkStallReasonNames.Count];
        private readonly long[] m_stalledTotal = new long[ChunkStallReasonNames.Count];
        private long m_nowMs;

        public ChunkResidencyTracker(int graceMs = DefaultGraceMs)
        {
            m_graceMs = graceMs < 0 ? 0 : graceMs;
        }

        /// <summary>块在内存里超过这么久还没定稿 → 排除出上报集合（毫秒）。</summary>
        public int GraceMs => m_graceMs;

        // ---- 当前计数（状态行直接打印）----
        /// <summary>引擎已分配的块数（内存集合）。</summary>
        public int MemoryCount => m_memory.Count;
        /// <summary>内容已定稿的块数（常驻；摘要 / 裁决口径）。</summary>
        public int ResidentCount => m_resident.Count;
        /// <summary>已在内存、但内容还没定稿的块数（= 实时地图上的灰框数，修复后应长期为 0）。</summary>
        public int MemoryNotResident => m_notReady.Count;
        /// <summary>真正参与"内存区块"上报的块数（= 内存 − 超时未定稿）。</summary>
        public int WireCount => m_wire.Count;
        /// <summary>被明确排除出上报集合的块数（超过宽限仍未定稿）。</summary>
        public int WireExcludedCount
        {
            get
            {
                int n = 0;
                foreach (KeyValuePair<ChunkKey, NotReady> pair in m_notReady)
                {
                    if (pair.Value.WireExcluded) n++;
                }
                return n;
            }
        }

        /// <summary>未定稿块数的历史峰值（诊断"曾经有多少灰框"）。</summary>
        public long MemoryNotResidentPeak { get; private set; }
        /// <summary>累计"内容定稿"次数（含卸载后重载的每一次 —— 重载必须再报一次）。</summary>
        public long ResidentFinalized { get; private set; }
        /// <summary>累计"内容掉回未定稿"次数。</summary>
        public long ResidentLost { get; private set; }
        /// <summary>累计"进入内存后又离开"的次数。</summary>
        public long FreedTotal { get; private set; }
        /// <summary>累计"已分配但未定稿"的块次数（按块生命周期计；不是每帧）。</summary>
        public long NotResidentSeen { get; private set; }
        /// <summary>累计"超过宽限被排除出上报集合"的次数（按原因分别见 <see cref="ContentGenStalled"/>）。</summary>
        public long WireExcludedTotal { get; private set; }
        /// <summary>累计"被排除后又定稿、重新纳回上报集合"的次数。</summary>
        public long WireRestoredTotal { get; private set; }

        /// <summary>累计停滞次数（按原因）：<c>contentGenStalled</c> 的 per-reason 账。</summary>
        public long ContentGenStalled(ChunkStallReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < m_stalledTotal.Length ? m_stalledTotal[index] : 0;
        }

        /// <summary>当前停滞块数（按原因）：此刻被排除、还没有定稿的块。</summary>
        public long ContentGenStalledNow(ChunkStallReason reason)
        {
            int index = (int)reason;
            return index >= 0 && index < m_stalledNow.Length ? m_stalledNow[index] : 0;
        }

        /// <summary>当前停滞总数（= <see cref="WireExcludedCount"/>）。</summary>
        public long ContentGenStalledNowTotal
        {
            get
            {
                long n = 0;
                for (int i = 0; i < m_stalledNow.Length; i++) n += m_stalledNow[i];
                return n;
            }
        }

        /// <summary>常驻（内容可用）区块：摘要 / 裁决 / 回传唯一认这一份。</summary>
        public IEnumerable<ChunkKey> ResidentChunks => m_resident;

        /// <summary>上报集合（内存 ∖ 超时未定稿）：实时地图"客户端内存区块"的唯一来源。</summary>
        public IEnumerable<ChunkKey> WireChunks => m_wire;

        /// <summary>本帧产生的状态变化（<see cref="BeginFrame"/> 之后可见；顺序 = 产生顺序）。</summary>
        public IReadOnlyList<ChunkResidencyChange> Changes => m_changes;

        /// <summary>开一帧：清本帧的"看到过"集合与变化列表。</summary>
        public void BeginFrame(long nowMs)
        {
            m_nowMs = nowMs;
            m_seen.Clear();
            m_changes.Clear();
        }

        /// <summary>
        /// 观测一块（每帧一次，已分配块逐个调用）。
        /// <paramref name="coveredByUpdateLocation"/>：null = 读不到更新位置（原因记
        /// <see cref="ChunkStallReason.LocationsUnknown"/>）；false = 没有任何更新位置覆盖它
        /// （引擎**不会**再推进它，属于真正的"无法定稿"）。
        /// </summary>
        public void Observe(ChunkKey key, ChunkContentStage stage, bool? coveredByUpdateLocation)
        {
            m_seen.Add(key);
            if (m_memory.Add(key))
                m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.Allocated,
                    ChunkStallReason.None, stage, 0L));
            // 上报集合的成员资格与内存同步：新分配的块默认参与上报。
            if (!m_notReady.TryGetValue(key, out NotReady info) || !info.WireExcluded)
                m_wire.Add(key);

            if (stage == ChunkContentStage.ContentValid)
            {
                if (!m_resident.Add(key)) return;                 // 已经是常驻：什么都没变
                ResidentFinalized++;
                m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.BecameResident,
                    ChunkStallReason.None, stage, 0L));
                if (m_notReady.TryGetValue(key, out NotReady was))
                {
                    m_notReady.Remove(key);
                    if (was.WireExcluded)
                    {
                        int index = (int)was.Reason;
                        if (index >= 0 && index < m_stalledNow.Length && m_stalledNow[index] > 0)
                            m_stalledNow[index]--;
                        // 之前被排除的块现在定稿了：重新纳回上报集合（明确事件，不静默恢复）。
                        m_wire.Add(key);
                        WireRestoredTotal++;
                        m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.WireRestored,
                            was.Reason, stage, m_nowMs - was.FirstMs));
                    }
                }
                return;
            }

            // ---- 未定稿 ----
            if (m_resident.Remove(key))
            {
                ResidentLost++;
                m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.BecameNotResident,
                    ChunkStallReason.None, stage, 0L));
            }
            ChunkStallReason reason = Classify(stage, coveredByUpdateLocation);
            if (!m_notReady.TryGetValue(key, out info))
            {
                info = new NotReady { FirstMs = m_nowMs, Reason = reason };
                NotResidentSeen++;
                if (m_notReady.Count + 1 > MemoryNotResidentPeak)
                    MemoryNotResidentPeak = m_notReady.Count + 1;
            }
            else if (info.Reason != reason && !info.WireExcluded)
            {
                // 原因会变（例如从"内容生成中"变成"没有更新位置"）：只更新原因，不重置计时。
                // 已经被判定"超时未定稿"之后**冻结**原因：per-reason 计数与告警行都以判定那一刻为准，
                // 否则计数会跟着原因漂移、恢复时还会减错桶。
                info.Reason = reason;
            }
            long age = m_nowMs - info.FirstMs;
            if (age < 0L) age = 0L;
            if (!info.WireExcluded && age >= m_graceMs)
            {
                info.WireExcluded = true;
                m_wire.Remove(key);
                WireExcludedTotal++;
                int index = (int)reason;
                if (index >= 0 && index < m_stalledNow.Length)
                {
                    m_stalledNow[index]++;
                    m_stalledTotal[index]++;
                }
                m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.WireExcluded, reason,
                    stage, age));
                m_newlyStalled.Add(new ChunkStallRecord(key, reason, stage, age));
            }
            m_notReady[key] = info;
        }

        /// <summary>
        /// 收帧：本帧**没被观测到**的块 = 引擎已释放 / 世界被卸载 → 先产生"内容不再可用"
        /// （若它还常驻），再产生"离开内存"。绝不静默丢弃集合里的条目。
        /// </summary>
        public void EndFrame()
        {
            if (m_memory.Count == 0) return;
            m_gone.Clear();
            foreach (ChunkKey key in m_memory)
            {
                if (!m_seen.Contains(key)) m_gone.Add(key);
            }
            for (int i = 0; i < m_gone.Count; i++)
            {
                ChunkKey key = m_gone[i];
                m_memory.Remove(key);
                m_wire.Remove(key);
                if (m_resident.Remove(key))
                {
                    ResidentLost++;
                    m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.BecameNotResident,
                        ChunkStallReason.None, ChunkContentStage.NotLoaded, 0L));
                }
                if (m_notReady.TryGetValue(key, out NotReady info))
                {
                    m_notReady.Remove(key);
                    if (info.WireExcluded)
                    {
                        int index = (int)info.Reason;
                        if (index >= 0 && index < m_stalledNow.Length && m_stalledNow[index] > 0)
                            m_stalledNow[index]--;
                    }
                }
                FreedTotal++;
                m_changes.Add(new ChunkResidencyChange(key, ChunkResidencyEvent.Freed,
                    ChunkStallReason.None, ChunkContentStage.NotLoaded, 0L));
            }
        }

        private readonly List<ChunkKey> m_gone = new List<ChunkKey>(64);

        /// <summary>
        /// 取走"本次新判为超时未定稿"的块（桥用它写限流 Warning；取走后清空）。
        /// 返回写入 <paramref name="buffer"/> 的条数（buffer 由调用方复用，不做分配）。
        /// </summary>
        public int TakeNewlyStalled(List<ChunkStallRecord> buffer)
        {
            if (buffer == null || m_newlyStalled.Count == 0) return 0;
            int count = 0;
            for (int i = 0; i < m_newlyStalled.Count; i++)
            {
                buffer.Add(m_newlyStalled[i]);
                count++;
            }
            m_newlyStalled.Clear();
            return count;
        }

        /// <summary>可审计的一行（状态行 / 日志直接打印；只列非零项）。</summary>
        public string Describe()
        {
            var builder = new StringBuilder(192);
            builder.Append("内存 ").Append(MemoryCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" · 常驻 ").Append(ResidentCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" · 上报集合 ").Append(WireCount.ToString(CultureInfo.InvariantCulture));
            builder.Append(" · memoryNotResident ").Append(MemoryNotResident.ToString(CultureInfo.InvariantCulture));
            builder.Append("（峰值 ").Append(MemoryNotResidentPeak.ToString(CultureInfo.InvariantCulture)).Append('）');
            builder.Append(" · residentFinalized ").Append(ResidentFinalized.ToString(CultureInfo.InvariantCulture));
            builder.Append(" · contentGenStalled ").Append(ContentGenStalledNowTotal.ToString(CultureInfo.InvariantCulture));
            builder.Append('（');
            bool any = false;
            for (int i = 1; i < ChunkStallReasonNames.Count; i++)
            {
                var reason = (ChunkStallReason)i;
                long now = ContentGenStalledNow(reason);
                long total = ContentGenStalled(reason);
                if (now == 0 && total == 0) continue;
                if (any) builder.Append('/');
                any = true;
                builder.Append(ChunkStallReasonNames.Of(reason));
                builder.Append(' ').Append(now.ToString(CultureInfo.InvariantCulture));
                builder.Append('/').Append(total.ToString(CultureInfo.InvariantCulture));
            }
            if (!any) builder.Append("none");
            builder.Append("）· 超时不上报 ").Append(WireExcludedTotal.ToString(CultureInfo.InvariantCulture));
            builder.Append(" · 恢复 ").Append(WireRestoredTotal.ToString(CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>世界卸载 / 换 Project：清空全部集合与计数（**只有显式换世界才调用**）。</summary>
        public void Reset()
        {
            m_memory.Clear();
            m_resident.Clear();
            m_wire.Clear();
            m_seen.Clear();
            m_notReady.Clear();
            m_changes.Clear();
            m_newlyStalled.Clear();
            m_gone.Clear();
            Array.Clear(m_stalledNow, 0, m_stalledNow.Length);
            Array.Clear(m_stalledTotal, 0, m_stalledTotal.Length);
            MemoryNotResidentPeak = 0;
            ResidentFinalized = 0;
            ResidentLost = 0;
            FreedTotal = 0;
            NotResidentSeen = 0;
            WireExcludedTotal = 0;
            WireRestoredTotal = 0;
        }

        private static ChunkStallReason Classify(ChunkContentStage stage, bool? coveredByUpdateLocation)
        {
            if (coveredByUpdateLocation == false) return ChunkStallReason.NoUpdateLocation;
            if (!coveredByUpdateLocation.HasValue) return ChunkStallReason.LocationsUnknown;
            return stage == ChunkContentStage.NotLoaded
                ? ChunkStallReason.Serializer
                : ChunkStallReason.Contents;
        }
    }
}

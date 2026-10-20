using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Google.Protobuf;
using PM = SuMultiplayer.Protocol.Melee;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 战斗随机数：**与引擎 <c>Engine/Engine/Random.cs</c> 完全相同的 LCG**
    /// （multiplier 25214903917 / addend 11 / mask 0xFFFFFFFFFFFF，
    /// <c>Int() = state &gt;&gt; 17</c>，<c>Float() = Int() / 2.147484e9f</c>，
    /// <c>Bool(p) = Float() &lt; p</c>）。
    ///
    /// 主机按 <c>(clientId, slot)</c> 持有**一条**流，客户端镜像同一条流：
    /// 客户端在攻击当帧就能算出主机将要用的 <c>u</c>，因此命中 / 未命中可以 0 ms 表现；
    /// 伤害仍然只由主机结算（客户端不预测数字、绝不写生命）。
    ///
    /// Source: Engine/Engine/Random.cs:1-90
    /// </summary>
    public sealed class CombatRng
    {
        /// <summary>Source: Engine/Engine/Random.cs:13（m_multiplier）</summary>
        public const ulong Multiplier = 25214903917UL;

        /// <summary>Source: Engine/Engine/Random.cs:15（m_addend）</summary>
        public const ulong Addend = 11UL;

        /// <summary>Source: Engine/Engine/Random.cs:17（m_mask）</summary>
        public const ulong StateMask = 281474976710655UL;

        /// <summary>Source: Engine/Engine/Random.cs:58（2.147484E+09f）</summary>
        public const float FloatDivisor = 2.147484E+09f;

        /// <summary>Source: Engine/Engine/Random.cs:38（0x5DEECE66D）</summary>
        public const ulong SeedXor = 0x5DEECE66DUL;

        private ulong m_state;

        public CombatRng(ulong state)
        {
            m_state = state;
        }

        /// <summary>
        /// 与 <c>new Engine.Random(int seed)</c> 同种子（<c>state = (ulong)seed ^ 0x5DEECE66D</c>）。
        /// 负数种子按 <c>(ulong)(long)seed</c> 符号扩展，与引擎一致。
        /// </summary>
        public static CombatRng FromEngineSeed(int seed) =>
            new CombatRng((ulong)(long)seed ^ SeedXor);

        public ulong State
        {
            get => m_state;
            set => m_state = value;
        }

        /// <summary>Source: Engine/Engine/Random.cs:53-57</summary>
        public int Int()
        {
            m_state = (m_state * Multiplier + Addend) & StateMask;
            return (int)(m_state >> 17);
        }

        /// <summary>Source: Engine/Engine/Random.cs:56-59</summary>
        public float Float() => (float)Int() / FloatDivisor;

        /// <summary>Source: Engine/Engine/Random.cs:47-50</summary>
        public bool Bool(float probability) => (float)Int() / FloatDivisor < probability;

        /// <summary>Source: Engine/Engine/Random.cs:61-64</summary>
        public float Float(float min, float max) => min + Float() * (max - min);

        /// <summary>
        /// 在第 <paramref name="steps"/> 次 <see cref="Int"/> 之前的内部状态（O(log steps)），
        /// 用于从会话种子直接跳到 <c>attackIndex</c>，不需要重放整条历史。
        /// </summary>
        public static ulong StateAdvance(ulong state, ulong steps)
        {
            // 仿射变换 f(x)=mul*x+add（mod 2^48）的快速幂：先算 steps 次的复合变换，再作用一次。
            ulong accumulatedMultiplier = 1UL;
            ulong accumulatedAddend = 0UL;
            ulong currentMultiplier = Multiplier;
            ulong currentAddend = Addend;
            while (steps != 0UL)
            {
                if ((steps & 1UL) != 0UL)
                {
                    // accumulated 之后接 current：x -> current(accumulated(x))
                    accumulatedMultiplier = (currentMultiplier * accumulatedMultiplier) & StateMask;
                    accumulatedAddend = (currentMultiplier * accumulatedAddend + currentAddend) &
                        StateMask;
                }
                currentAddend = (currentAddend * currentMultiplier + currentAddend) & StateMask;
                currentMultiplier = (currentMultiplier * currentMultiplier) & StateMask;
                steps >>= 1;
            }
            return (accumulatedMultiplier * state + accumulatedAddend) & StateMask;
        }

        /// <summary>从任意状态开始按引擎语义抽 <paramref name="count"/> 个 Int（测试 / 审计用）。</summary>
        public int[] Take(int count)
        {
            if (count <= 0) return Array.Empty<int>();
            var values = new int[count];
            for (int i = 0; i < count; i++) values[i] = Int();
            return values;
        }
    }

    /// <summary>
    /// 一次已接受攻击固定占用的 4 个随机槽：
    ///   · [0] <see cref="U"/>     命中均匀值（0..1）
    ///   · [1] <see cref="F"/>     伤害方差 f（0.8..1.2）
    ///   · [2] <see cref="A"/>     护甲槽随机值（0..1，当前引擎护甲是确定性的，仅记录 / 供未来回放）
    ///   · [3] <see cref="Reserved"/> 预留（0..1）
    /// 拒绝的攻击**不抽槽、不推进 index**。
    /// </summary>
    public readonly struct CombatSlotRolls
    {
        public const float MinVariance = 0.8f;
        public const float MaxVariance = 1.2f;

        public CombatSlotRolls(long attackIndex, float u, float f, float a, float reserved)
        {
            AttackIndex = attackIndex;
            U = u;
            F = f;
            A = a;
            Reserved = reserved;
        }

        public long AttackIndex { get; }
        public float U { get; }
        public float F { get; }
        public float A { get; }
        public float Reserved { get; }

        public bool IsValid =>
            float.IsFinite(U) && U >= 0f && U <= 1f &&
            float.IsFinite(F) && F >= MinVariance && F <= MaxVariance &&
            float.IsFinite(A) && A >= 0f && A <= 1f &&
            float.IsFinite(Reserved) && Reserved >= 0f && Reserved <= 1f;

        public string Describe()
        {
            return "idx=" + AttackIndex + " u=" + Fmt(U) + " f=" + Fmt(F) + " a=" + Fmt(A) +
                " r=" + Fmt(Reserved);
        }

        private static string Fmt(float value) =>
            float.IsFinite(value) ? value.ToString("0.####", CultureInfo.InvariantCulture) : "NaN";
    }

    /// <summary>
    /// 一条玩家战斗流：会话种子 + 下一个 attackIndex。每次接受攻击消耗 4 个 Int，
    /// 所以第 n 次攻击的 4 个槽就是种子流里的第 <c>4n..4n+3</c> 个值（可用
    /// <see cref="CombatRng.StateAdvance"/> 直接跳过去）。
    /// </summary>
    public sealed class CombatRngStream
    {
        public const int SlotsPerAttack = 4;

        private CombatRngStream(ulong seed, long nextIndex)
        {
            Seed = seed;
            NextIndex = nextIndex;
        }

        public CombatRngStream(ulong seed) : this(seed, 0L)
        {
        }

        public static CombatRngStream FromState(ulong seed, long nextIndex) =>
            new CombatRngStream(seed, nextIndex);

        public ulong Seed { get; }
        public long NextIndex { get; private set; }

        /// <summary>下一次攻击会用的 4 个槽（不推进流）。</summary>
        public CombatSlotRolls Peek()
        {
            CombatRng generator = GeneratorAt(NextIndex);
            return Draw(ref generator, NextIndex);
        }

        /// <summary>消耗一次攻击的 4 个槽并推进 index（只有已接受攻击才调用）。</summary>
        public CombatSlotRolls Consume()
        {
            CombatRng generator = GeneratorAt(NextIndex);
            CombatSlotRolls rolls = Draw(ref generator, NextIndex);
            NextIndex++;
            return rolls;
        }

        /// <summary>把 index 设到主机给的位置（重连 / 重同步：跳过缺失的结果，不重放）。</summary>
        public void SetIndex(long index)
        {
            if (index < 0L)
                throw new ArgumentOutOfRangeException(nameof(index), index, "attackIndex 不能为负");
            NextIndex = index;
        }

        /// <summary>抽第 <paramref name="index"/> 次攻击的 4 个槽（无副作用）。</summary>
        public CombatSlotRolls RollsAt(long index)
        {
            CombatRng generator = GeneratorAt(index);
            return Draw(ref generator, index);
        }

        private CombatRng GeneratorAt(long index)
        {
            if (index < 0L)
                throw new ArgumentOutOfRangeException(nameof(index), index, "attackIndex 不能为负");
            // 每次攻击 4 个 Int：直接把 LCG 状态推进 index*4 步。
            ulong steps = (ulong)index * (ulong)SlotsPerAttack;
            return new CombatRng(CombatRng.StateAdvance(Seed, steps));
        }

        private static CombatSlotRolls Draw(ref CombatRng generator, long index)
        {
            float u = generator.Float();
            float f = generator.Float(CombatSlotRolls.MinVariance, CombatSlotRolls.MaxVariance);
            float a = generator.Float();
            float reserved = generator.Float();
            return new CombatSlotRolls(index, u, f, a, reserved);
        }
    }

    /// <summary>
    /// 命中概率的**唯一公式**（主客两端同一份）：
    ///   p = (有手持物 ? block.GetMeleeHitProbability(value) : 0.66)
    ///       × (目标速度 &lt; 0.05 ? 3 : 1)
    /// Source: Survivalcraft/Game/ComponentMiner.cs:309-318
    /// </summary>
    public static class CombatHitProbability
    {
        public const float NoToolProbability = 0.66f;
        public const float SlowTargetSpeed = 0.05f;
        public const float SlowTargetMultiplier = 3f;

        public static float Base(float blockProbability, bool hasTool) =>
            hasTool ? blockProbability : NoToolProbability;

        public static float Apply(float baseProbability, float targetSpeed)
        {
            float probability = baseProbability *
                (targetSpeed < SlowTargetSpeed ? SlowTargetMultiplier : 1f);
            return probability;
        }

        public static float Compute(bool hasTool, float blockProbability, float targetSpeed) =>
            Apply(Base(blockProbability, hasTool), targetSpeed);

        /// <summary>引擎 <c>m_random.Bool(p)</c> 的等价判定：<c>u &lt; p</c>（p &gt; 1 恒命中）。</summary>
        public static bool Hit(float u, float probability) => u < probability;
    }

    /// <summary>主机 / 客户端共用的结算结论（纯值）。</summary>
    public readonly struct CombatSettlementResult
    {
        public CombatSettlementResult(CombatSlotRolls rolls, float probability, bool hit, float power)
        {
            Rolls = rolls;
            Probability = probability;
            Hit = hit;
            Power = power;
        }

        public CombatSlotRolls Rolls { get; }
        public float Probability { get; }
        public bool Hit { get; }
        /// <summary>引擎口径的攻击力：meleePower × AttackPower × f × StrengthFactor。</summary>
        public float Power { get; }
    }

    /// <summary>
    /// 主机结算的**纯数学部分**（引擎适配层调用它，测试直接断言）：
    /// 消耗一次战斗流，命中判定 = <c>u &lt; p</c>，攻击力 = meleePower × AttackPower × f × StrengthFactor。
    /// 其余（护甲 / 韧性 / Injure / 击退 / 工具磨损 / 统计）仍走引擎公开路径。
    ///
    /// Source: Survivalcraft/Game/ComponentMiner.cs:309-326
    /// </summary>
    public static class CombatSettlement
    {
        public static CombatSettlementResult Settle(CombatRngStream stream, float baseProbability,
            float targetSpeed, float meleePower, float attackPower, float strengthFactor)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!float.IsFinite(baseProbability))
                throw new ArgumentOutOfRangeException(nameof(baseProbability), baseProbability,
                    "命中概率必须有限");
            if (!float.IsFinite(meleePower) || !float.IsFinite(attackPower) ||
                !float.IsFinite(strengthFactor))
                throw new ArgumentOutOfRangeException(nameof(meleePower), "攻击力因子必须有限");
            CombatSlotRolls rolls = stream.Consume();
            float probability = CombatHitProbability.Apply(baseProbability, targetSpeed);
            bool hit = CombatHitProbability.Hit(rolls.U, probability);
            float power = meleePower * attackPower * rolls.F * strengthFactor;
            return new CombatSettlementResult(rolls, probability, hit, power);
        }
    }

    /// <summary>战斗流键：攻击者 <c>(clientId, slot)</c>（主机本机 = clientId 0）。</summary>
    public readonly struct CombatRngKey : IEquatable<CombatRngKey>
    {
        public CombatRngKey(int clientId, int slot)
        {
            ClientId = clientId;
            Slot = slot;
        }

        public int ClientId { get; }
        public int Slot { get; }

        public bool Equals(CombatRngKey other) => ClientId == other.ClientId && Slot == other.Slot;
        public override bool Equals(object obj) => obj is CombatRngKey other && Equals(other);
        public override int GetHashCode() => (ClientId * 397) ^ Slot;

        public override string ToString() => ClientId + ":" + Slot;
    }

    /// <summary>COMBAT_RNG_SYNC 的一条流（主机 → 客户端）。</summary>
    public readonly struct CombatRngSyncEntry
    {
        public CombatRngSyncEntry(int clientId, int slot, ulong seed, long index)
        {
            ClientId = clientId;
            Slot = slot;
            Seed = seed;
            Index = index;
        }

        public int ClientId { get; }
        public int Slot { get; }
        public ulong Seed { get; }
        public long Index { get; }
    }

    /// <summary>COMBAT_RNG_SYNC 的纯值载荷。</summary>
    public readonly struct CombatRngSyncData
    {
        public CombatRngSyncData(long serverTick, bool reset, CombatRngSyncEntry[] streams)
        {
            ServerTick = serverTick;
            Reset = reset;
            Streams = streams ?? Array.Empty<CombatRngSyncEntry>();
        }

        public long ServerTick { get; }
        /// <summary>true = 重连：客户端丢弃旧流，按 seed + index 重建（不重放缺失结果）。</summary>
        public bool Reset { get; }
        public CombatRngSyncEntry[] Streams { get; }
    }

    /// <summary>COMBAT_RNG_SYNC 编解码（append-only id 99；严格校验，畸形整条拒绝）。</summary>
    public static class CombatRngCodec
    {
        public const int MaxStreams = 8;
        public const int MaxPayloadBytes = 2048;

        public static bool TryEncode(in CombatRngSyncData data, out PM.CombatRngSync message,
            out string reason)
        {
            message = null;
            if (!TryValidate(in data, out reason)) return false;
            message = new PM.CombatRngSync { ServerTick = data.ServerTick, Reset = data.Reset };
            for (int i = 0; i < data.Streams.Length; i++)
            {
                CombatRngSyncEntry entry = data.Streams[i];
                message.Streams.Add(new PM.CombatRngStream
                {
                    ClientId = entry.ClientId,
                    Slot = entry.Slot,
                    Seed = entry.Seed,
                    Index = entry.Index
                });
            }
            return true;
        }

        public static byte[] Encode(in CombatRngSyncData data)
        {
            if (!TryEncode(in data, out PM.CombatRngSync message, out string reason))
                throw new InvalidOperationException("战斗随机流同步编解码失败：" + reason);
            return message.ToByteArray();
        }

        public static PM.CombatRngSync EncodeMessage(in CombatRngSyncData data)
        {
            if (!TryEncode(in data, out PM.CombatRngSync message, out string reason))
                throw new InvalidOperationException("战斗随机流同步编解码失败：" + reason);
            return message;
        }

        public static bool TryDecode(PM.CombatRngSync message, out CombatRngSyncData data,
            out string reason)
        {
            data = default;
            reason = null;
            if (message == null)
            {
                reason = "combat-rng-sync-null";
                return false;
            }
            if (message.Ext != null && message.Ext.Length > 0)
            {
                reason = "combat-rng-sync-ext-length-" + message.Ext.Length;
                return false;
            }
            if (message.Streams.Count > MaxStreams)
            {
                reason = "combat-rng-sync-too-many-" + message.Streams.Count;
                return false;
            }
            var entries = new CombatRngSyncEntry[message.Streams.Count];
            var seen = new HashSet<CombatRngKey>();
            for (int i = 0; i < message.Streams.Count; i++)
            {
                PM.CombatRngStream stream = message.Streams[i];
                if (stream == null)
                {
                    reason = "combat-rng-sync-stream-null";
                    return false;
                }
                if (stream.ClientId < 0 || stream.ClientId > MeleeLimits.MaxClientId)
                {
                    reason = "combat-rng-sync-client-" + stream.ClientId;
                    return false;
                }
                if (stream.Slot < 0 || stream.Slot > Players.PlayerKey.MaxSlot)
                {
                    reason = "combat-rng-sync-slot-" + stream.Slot;
                    return false;
                }
                if (stream.Index < 0 || stream.Index > MeleeLimits.MaxAttackIndex)
                {
                    reason = "combat-rng-sync-index-" + stream.Index;
                    return false;
                }
                var key = new CombatRngKey(stream.ClientId, stream.Slot);
                if (!seen.Add(key))
                {
                    reason = "combat-rng-sync-duplicate-" + key;
                    return false;
                }
                entries[i] = new CombatRngSyncEntry(stream.ClientId, stream.Slot, stream.Seed,
                    stream.Index);
            }
            if (message.ServerTick < 0)
            {
                reason = "combat-rng-sync-tick-" + message.ServerTick;
                return false;
            }
            data = new CombatRngSyncData(message.ServerTick, message.Reset, entries);
            return true;
        }

        public static bool TryDecode(byte[] payload, out CombatRngSyncData data, out string reason)
        {
            data = default;
            reason = null;
            if (payload == null || payload.Length == 0)
            {
                reason = "combat-rng-sync-empty";
                return false;
            }
            if (payload.Length > MaxPayloadBytes)
            {
                reason = "combat-rng-sync-too-long-" + payload.Length;
                return false;
            }
            PM.CombatRngSync message;
            try
            {
                message = PM.CombatRngSync.Parser.ParseFrom(payload);
            }
            catch (Exception e)
            {
                reason = "combat-rng-sync-parse-" + e.GetType().Name;
                return false;
            }
            return TryDecode(message, out data, out reason);
        }

        private static bool TryValidate(in CombatRngSyncData data, out string reason)
        {
            reason = null;
            if (data.ServerTick < 0)
            {
                reason = "combat-rng-sync-tick-" + data.ServerTick;
                return false;
            }
            if (data.Streams == null || data.Streams.Length > MaxStreams)
            {
                reason = "combat-rng-sync-too-many";
                return false;
            }
            var seen = new HashSet<CombatRngKey>();
            for (int i = 0; i < data.Streams.Length; i++)
            {
                CombatRngSyncEntry entry = data.Streams[i];
                if (entry.ClientId < 0 || entry.ClientId > MeleeLimits.MaxClientId)
                {
                    reason = "combat-rng-sync-client-" + entry.ClientId;
                    return false;
                }
                if (entry.Slot < 0 || entry.Slot > Players.PlayerKey.MaxSlot)
                {
                    reason = "combat-rng-sync-slot-" + entry.Slot;
                    return false;
                }
                if (entry.Index < 0 || entry.Index > MeleeLimits.MaxAttackIndex)
                {
                    reason = "combat-rng-sync-index-" + entry.Index;
                    return false;
                }
                if (!seen.Add(new CombatRngKey(entry.ClientId, entry.Slot)))
                {
                    reason = "combat-rng-sync-duplicate-" + entry.ClientId + ":" + entry.Slot;
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 主机侧：每个 <c>(clientId, slot)</c> 一条战斗流。种子来自主机会话 RNG
    /// （组合根传入 <c>Func&lt;ulong&gt;</c>），保证每次会话的序列不可预测但与客户端共享。
    /// </summary>
    public sealed class CombatRngHostState
    {
        private readonly Func<ulong> m_seedFactory;
        private readonly Dictionary<CombatRngKey, CombatRngStream> m_streams =
            new Dictionary<CombatRngKey, CombatRngStream>();
        private readonly CombatRngTelemetry m_telemetry;

        public CombatRngHostState(Func<ulong> seedFactory, CombatRngTelemetry telemetry = null)
        {
            m_seedFactory = seedFactory ?? throw new ArgumentNullException(nameof(seedFactory));
            m_telemetry = telemetry;
        }

        public int Count => m_streams.Count;

        public CombatRngStream Ensure(in CombatRngKey key)
        {
            if (m_streams.TryGetValue(key, out CombatRngStream stream) && stream != null)
                return stream;
            ulong seed = m_seedFactory();
            stream = new CombatRngStream(seed);
            m_streams[key] = stream;
            return stream;
        }

        public bool TryGet(in CombatRngKey key, out CombatRngStream stream) =>
            m_streams.TryGetValue(key, out stream) && stream != null;

        public long NextIndex(in CombatRngKey key) => Ensure(in key).NextIndex;

        /// <summary>已接受攻击：抽 4 个槽并推进；拒绝路径**不要**调用。</summary>
        public CombatSlotRolls Consume(in CombatRngKey key)
        {
            CombatSlotRolls rolls = Ensure(in key).Consume();
            if (m_telemetry != null) m_telemetry.CombatRngIndex = rolls.AttackIndex + 1;
            return rolls;
        }

        /// <summary>为该玩家端的 0..MaxSlot 建流（加入 / 重连时主机主动调用）。</summary>
        public int EnsureClient(int clientId)
        {
            int count = 0;
            for (int slot = 0; slot <= Players.PlayerKey.MaxSlot; slot++)
            {
                Ensure(new CombatRngKey(clientId, slot));
                count++;
            }
            return count;
        }

        public int RemoveClient(int clientId)
        {
            var remove = new List<CombatRngKey>();
            foreach (CombatRngKey key in m_streams.Keys)
                if (key.ClientId == clientId) remove.Add(key);
            for (int i = 0; i < remove.Count; i++) m_streams.Remove(remove[i]);
            return remove.Count;
        }

        public CombatRngSyncEntry[] SnapshotClient(int clientId)
        {
            var entries = new List<CombatRngSyncEntry>(Players.PlayerKey.SlotCount);
            for (int slot = 0; slot <= Players.PlayerKey.MaxSlot; slot++)
            {
                var key = new CombatRngKey(clientId, slot);
                if (!m_streams.TryGetValue(key, out CombatRngStream stream) || stream == null)
                    continue;
                entries.Add(new CombatRngSyncEntry(clientId, slot, stream.Seed, stream.NextIndex));
            }
            return entries.ToArray();
        }

        public void Reset()
        {
            m_streams.Clear();
        }

        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            foreach (KeyValuePair<CombatRngKey, CombatRngStream> pair in m_streams)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(pair.Key).Append('@').Append(pair.Value.NextIndex);
            }
            return builder.Length == 0 ? "none" : builder.ToString();
        }
    }

    /// <summary>客户端对账结论。</summary>
    public enum CombatRngReconcile
    {
        /// <summary>index 与预测一致（且流可用）。</summary>
        Match = 0,
        /// <summary>主机实际用的 index / 流位置与客户端预测不一致：已回滚并重同步。</summary>
        Mismatch = 1,
        /// <summary>客户端还没有这条流（未收到同步）：无法对账，等 COMBAT_RNG_SYNC。</summary>
        NoStream = 2
    }

    /// <summary>
    /// 客户端镜像：保存主机下发的种子 + index，攻击当帧预测命中，结果到达时对账 / 重同步。
    ///
    /// 规则（与主机一致）：
    ///   · 每次**已接受**攻击消耗一个 index（4 个槽）；拒绝不消耗；
    ///   · 客户端在发请求时**乐观**推进 index（下一发攻击立刻用下一个 index）；
    ///   · 结果里的 <c>stream_index</c> 是主机权威位置：对账后一律以它为准
    ///     （拒绝导致回退、缺结果导致跳过，都不重放）。
    /// </summary>
    public sealed class CombatRngClientState
    {
        private sealed class SlotStream
        {
            public int ClientId;
            public CombatRngStream Stream;
        }

        private readonly Dictionary<int, SlotStream> m_slots = new Dictionary<int, SlotStream>();
        private readonly CombatRngTelemetry m_telemetry;

        public CombatRngClientState(CombatRngTelemetry telemetry = null)
        {
            m_telemetry = telemetry;
        }

        public int LocalClientId { get; private set; }

        public int StreamCount => m_slots.Count;

        public void Reset()
        {
            m_slots.Clear();
            LocalClientId = 0;
        }

        public bool HasStream(int slot) => m_slots.ContainsKey(slot);

        public long IndexOf(int slot) =>
            m_slots.TryGetValue(slot, out SlotStream stream) && stream != null
                ? stream.Stream.NextIndex : 0L;

        /// <summary>
        /// 应用主机同步：<paramref name="reset"/>（重连）= 丢弃旧流按 seed+index 重建；
        /// 否则同种子只把 index 校正到主机位置（比主机旧 = 跳过缺失结果，绝不重放）。
        /// 返回实际覆盖的流条数；<paramref name="reason"/> 非空 = 整条拒绝。
        /// </summary>
        public bool ApplySync(int localClientId, CombatRngSyncEntry[] entries, bool reset,
            out int applied, out string reason)
        {
            applied = 0;
            reason = null;
            if (localClientId <= 0)
            {
                reason = "本地传输编号还没分配（LocalClientId=" + localClientId + "）";
                return false;
            }
            if (entries == null)
            {
                reason = "同步载荷没有流列表";
                return false;
            }
            // 先整包校验，再一次性应用：畸形同步不能留下半套流。
            var valid = new List<CombatRngSyncEntry>(entries.Length);
            var seen = new HashSet<int>();
            for (int i = 0; i < entries.Length; i++)
            {
                CombatRngSyncEntry entry = entries[i];
                if (entry.ClientId != localClientId) continue;
                if (entry.Slot < 0 || entry.Slot > Players.PlayerKey.MaxSlot)
                {
                    reason = "同步流的槽位非法：" + entry.Slot;
                    return false;
                }
                if (entry.Index < 0L || entry.Index > MeleeLimits.MaxAttackIndex)
                {
                    reason = "同步流的 index 非法：" + entry.Index;
                    return false;
                }
                if (!seen.Add(entry.Slot))
                {
                    reason = "同步流槽位重复：" + entry.Slot;
                    return false;
                }
                valid.Add(entry);
            }
            if (valid.Count == 0)
            {
                reason = "同步载荷没有本地 clientId=" + localClientId + " 的流";
                return false;
            }
            bool clientChanged = LocalClientId != localClientId;
            if (reset || clientChanged) m_slots.Clear();
            LocalClientId = localClientId;
            long highest = 0L;
            for (int i = 0; i < valid.Count; i++)
            {
                CombatRngSyncEntry entry = valid[i];
                bool changed;
                if (!m_slots.TryGetValue(entry.Slot, out SlotStream old) || old == null ||
                    old.ClientId != localClientId || old.Stream.Seed != entry.Seed)
                {
                    m_slots[entry.Slot] = new SlotStream
                    {
                        ClientId = localClientId,
                        Stream = new CombatRngStream(entry.Seed)
                    };
                    m_slots[entry.Slot].Stream.SetIndex(entry.Index);
                    changed = true;
                }
                else
                {
                    changed = old.Stream.NextIndex != entry.Index;
                    old.Stream.SetIndex(entry.Index);
                }
                if (changed && m_telemetry != null) m_telemetry.CombatRngResync++;
                applied++;
                if (entry.Index > highest) highest = entry.Index;
            }
            if (m_telemetry != null) m_telemetry.CombatRngIndex = highest;
            return true;
        }

        /// <summary>
        /// 攻击当帧取下一发 index 与 4 个槽，并乐观推进 index。
        /// false = 还没有流：调用方必须**不预测**（计数 predictionUnavailable），仍可把
        /// <paramref name="attackIndex"/> 作为 0 上行；主机会发同步纠正。
        /// </summary>
        public bool TryBeginAttack(int slot, out long attackIndex, out CombatSlotRolls rolls,
            out string reason)
        {
            reason = null;
            if (!m_slots.TryGetValue(slot, out SlotStream stream) || stream == null)
            {
                attackIndex = 0L;
                rolls = default;
                reason = "no-stream";
                return false;
            }
            CombatRngStream state = stream.Stream;
            attackIndex = state.NextIndex;
            rolls = state.Peek();
            state.SetIndex(attackIndex + 1L);
            if (m_telemetry != null) m_telemetry.CombatRngIndex = state.NextIndex;
            return true;
        }

        /// <summary>请求没有发出去：撤销乐观推进（主机不会消耗这个 index）。</summary>
        public void Rewind(int slot, long attackIndex)
        {
            if (attackIndex < 0L) return;
            if (m_slots.TryGetValue(slot, out SlotStream stream) && stream != null)
            {
                if (stream.Stream.NextIndex == attackIndex + 1L)
                    stream.Stream.SetIndex(attackIndex);
            }
        }

        /// <summary>
        /// 用主机结果对账：结果里的 <c>stream_index</c> 是权威位置，一律以它为准。
        /// <paramref name="sentIndex"/> = 客户端请求里带的 index；主机实际用的
        /// <paramref name="resultAttackIndex"/> 与它不同 = <see cref="CombatRngReconcile.Mismatch"/>。
        /// </summary>
        public CombatRngReconcile Reconcile(int slot, long sentIndex, long resultAttackIndex,
            long streamIndex, bool accepted)
        {
            if (!m_slots.TryGetValue(slot, out SlotStream stream) || stream == null)
                return CombatRngReconcile.NoStream;
            CombatRngStream state = stream.Stream;
            bool mismatch = sentIndex >= 0L && resultAttackIndex != sentIndex;
            bool changed = state.NextIndex != streamIndex;
            if (streamIndex >= 0L) state.SetIndex(streamIndex);
            if (m_telemetry != null)
            {
                m_telemetry.CombatRngIndex = state.NextIndex;
                if (mismatch) m_telemetry.CombatRngMismatch++;
                if (mismatch || changed) m_telemetry.CombatRngResync++;
            }
            _ = accepted;
            return mismatch ? CombatRngReconcile.Mismatch : CombatRngReconcile.Match;
        }

        public string Describe()
        {
            var builder = new System.Text.StringBuilder();
            foreach (KeyValuePair<int, SlotStream> pair in m_slots)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(pair.Key).Append('@').Append(pair.Value.Stream.NextIndex);
            }
            return "client=" + LocalClientId + " streams=" +
                (builder.Length == 0 ? "none" : builder.ToString());
        }
    }

    /// <summary>战斗随机 / 预测计数（模块与引擎共享同一实例，状态行直接读）。</summary>
    public sealed class CombatRngTelemetry
    {
        public long CombatRngSyncSent;
        public long CombatRngSyncRecv;
        public long CombatRngResync;
        public long CombatRngIndex;
        public long CombatRngMismatch;
        public long PredictedHit;
        public long PredictedHitConfirmed;
        public long PredictedHitRollback;
        public long PredictedMiss;
        public long PredictedMissConfirmed;
        public long PredictionUnavailable;
        public long AttackLogStored;
        public long AttackLogDropped;
        public long LagCompSample;
        public long LagCompBufferFull;
        public long LagCompNotReady;

        public string Describe() => "combatRngSyncSent=" + CombatRngSyncSent +
            " combatRngSyncRecv=" + CombatRngSyncRecv +
            " combatRngResync=" + CombatRngResync +
            " combatRngIndex=" + CombatRngIndex +
            " combatRngMismatch=" + CombatRngMismatch +
            " predictedHit=" + PredictedHit +
            " predictedHitConfirmed=" + PredictedHitConfirmed +
            " predictedHitRollback=" + PredictedHitRollback +
            " predictedMiss=" + PredictedMiss +
            " predictedMissConfirmed=" + PredictedMissConfirmed +
            " predictionUnavailable=" + PredictionUnavailable +
            " attackLogStored=" + AttackLogStored +
            " attackLogDropped=" + AttackLogDropped +
            " lagCompSample=" + LagCompSample +
            " lagCompBufferFull=" + LagCompBufferFull +
            " lagCompNotReady=" + LagCompNotReady;
    }

    /// <summary>一次主机结算的审计记录（回放 / 对账用）。</summary>
    public readonly struct CombatAttackLogEntry
    {
        public CombatAttackLogEntry(long sequence, long attackIndex, float u, float f, float a,
            float reserved, string target, float probability, bool hit, float damage, string reason)
        {
            Sequence = sequence;
            AttackIndex = attackIndex;
            U = u;
            F = f;
            A = a;
            Reserved = reserved;
            Target = target ?? string.Empty;
            Probability = probability;
            Hit = hit;
            Damage = damage;
            Reason = reason ?? string.Empty;
        }

        public long Sequence { get; }
        public long AttackIndex { get; }
        public float U { get; }
        public float F { get; }
        public float A { get; }
        public float Reserved { get; }
        public string Target { get; }
        public float Probability { get; }
        public bool Hit { get; }
        public float Damage { get; }
        public string Reason { get; }

        public string Describe() =>
            "seq=" + Sequence + " idx=" + AttackIndex + " u=" + Fmt(U) + " f=" + Fmt(F) +
            " a=" + Fmt(A) + " r=" + Fmt(Reserved) + " target=" + Target + " p=" + Fmt(Probability) +
            " " + (Hit ? "hit" : "miss") + " dmg=" + Fmt(Damage) +
            (string.IsNullOrEmpty(Reason) ? string.Empty : " reason=" + Reason);

        private static string Fmt(float value) =>
            float.IsFinite(value) ? value.ToString("0.####", CultureInfo.InvariantCulture) : "NaN";
    }

    /// <summary>
    /// 有界攻击审计环（默认最近 256 条）：attackIndex / u,f,a,r / 目标 / p / 结果 / 伤害。
    /// 满了覆盖最旧一条并计 <c>attackLogDropped</c>；<c>attackLogStored</c> 计写入总次数。
    /// </summary>
    public sealed class CombatAttackLog
    {
        public const int DefaultCapacity = 256;

        private readonly CombatAttackLogEntry[] m_entries;
        private readonly CombatRngTelemetry m_telemetry;
        private int m_start;
        private int m_count;

        public CombatAttackLog(CombatRngTelemetry telemetry = null, int capacity = DefaultCapacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            m_entries = new CombatAttackLogEntry[capacity];
            m_telemetry = telemetry;
        }

        public int Capacity => m_entries.Length;
        public int Count => m_count;
        public long Stored { get; private set; }
        public long Dropped { get; private set; }

        public void Store(in CombatAttackLogEntry entry)
        {
            if (m_count == m_entries.Length)
            {
                m_entries[m_start] = entry;
                m_start = (m_start + 1) % m_entries.Length;
                Dropped++;
            }
            else
            {
                m_entries[(m_start + m_count) % m_entries.Length] = entry;
                m_count++;
            }
            Stored++;
            if (m_telemetry != null)
            {
                m_telemetry.AttackLogStored = Stored;
                m_telemetry.AttackLogDropped = Dropped;
            }
        }

        public bool TryGetNewest(out CombatAttackLogEntry entry)
        {
            if (m_count == 0)
            {
                entry = default;
                return false;
            }
            entry = m_entries[(m_start + m_count - 1) % m_entries.Length];
            return true;
        }

        /// <summary>按时间顺序（旧 → 新）复制最近的有界条目。</summary>
        public CombatAttackLogEntry[] Snapshot()
        {
            var result = new CombatAttackLogEntry[m_count];
            for (int i = 0; i < m_count; i++)
                result[i] = m_entries[(m_start + i) % m_entries.Length];
            return result;
        }

        public void Clear()
        {
            Array.Clear(m_entries, 0, m_entries.Length);
            m_start = 0;
            m_count = 0;
            Stored = 0L;
            Dropped = 0L;
            if (m_telemetry != null)
            {
                m_telemetry.AttackLogStored = 0L;
                m_telemetry.AttackLogDropped = 0L;
            }
        }

        public string Describe() => TryGetNewest(out CombatAttackLogEntry newest)
            ? "attackLogStored=" + Stored + " attackLogDropped=" + Dropped + " attackLogCount=" +
              Count + " last=[" + newest.Describe() + "]"
            : "attackLogStored=" + Stored + " attackLogDropped=" + Dropped + " attackLogCount=0";
    }

    /// <summary>目标位置 / 速度采样（未来 lag compensation rewind 的输入）。</summary>
    public readonly struct CombatLagSample
    {
        public CombatLagSample(int targetId, long tick, Vector3 position, Vector3 velocity)
        {
            TargetId = targetId;
            Tick = tick;
            Position = position;
            Velocity = velocity;
        }

        public int TargetId { get; }
        public long Tick { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
    }

    /// <summary>
    /// lag compensation 未来钩子的接口（**本版只实现、不参与裁决**）：
    /// 主机记录目标位置 / 速度历史，未来按客户端攻击 tick 回看目标当时的位置。
    /// </summary>
    public interface ICombatLagCompensation
    {
        void Record(int targetId, long tick, Vector3 position, Vector3 velocity);
        bool TrySample(int targetId, long tick, out Vector3 position, out Vector3 velocity);
    }

    /// <summary>
    /// 主机目标位置 / 速度历史环（**只做 groundwork，不改变命中裁决**）。
    /// 每次主机结算一次已接受攻击时记录目标当前采样；未来 lag compensation 会用
    /// <see cref="TrySample"/> 按客户端 tick 回看目标位置。本版**不调用** TrySample 参与裁决。
    ///
    /// 计数：<c>lagCompSample</c>（成功采样）/ <c>lagCompBufferFull</c>（环满覆盖最旧）/
    /// <c>lagCompNotReady</c>（该目标该 tick 之前没有可用采样）。
    /// </summary>
    public sealed class CombatLagCompensationBuffer : ICombatLagCompensation
    {
        public const int DefaultCapacityPerTarget = 32;

        private readonly Dictionary<int, Queue<CombatLagSample>> m_samples =
            new Dictionary<int, Queue<CombatLagSample>>();
        private readonly CombatRngTelemetry m_telemetry;
        private readonly int m_capacity;

        public CombatLagCompensationBuffer(CombatRngTelemetry telemetry = null,
            int capacityPerTarget = DefaultCapacityPerTarget)
        {
            if (capacityPerTarget <= 0) throw new ArgumentOutOfRangeException(nameof(capacityPerTarget));
            m_telemetry = telemetry;
            m_capacity = capacityPerTarget;
        }

        public int CapacityPerTarget => m_capacity;

        public int TargetCount => m_samples.Count;

        /// <summary>成功采样次数（与 telemetry.lagCompSample 同步）。</summary>
        public long Sample { get; private set; }
        /// <summary>环满覆盖最旧采样的次数（与 telemetry.lagCompBufferFull 同步）。</summary>
        public long BufferFull { get; private set; }
        /// <summary>该目标该 tick 之前没有可用采样（与 telemetry.lagCompNotReady 同步）。</summary>
        public long NotReady { get; private set; }

        public void Record(int targetId, long tick, Vector3 position, Vector3 velocity)
        {
            if (!m_samples.TryGetValue(targetId, out Queue<CombatLagSample> queue))
            {
                queue = new Queue<CombatLagSample>(m_capacity);
                m_samples[targetId] = queue;
            }
            if (queue.Count >= m_capacity)
            {
                queue.Dequeue();
                BufferFull++;
                if (m_telemetry != null) m_telemetry.LagCompBufferFull++;
            }
            queue.Enqueue(new CombatLagSample(targetId, tick, position, velocity));
        }

        /// <summary>
        /// 取该目标 <paramref name="tick"/> 当时（&lt;= tick 的最新一条）的位置 / 速度。
        /// 未来 rewind 只允许用它；当前版本不参与命中判定。
        /// </summary>
        public bool TrySample(int targetId, long tick, out Vector3 position, out Vector3 velocity)
        {
            position = default;
            velocity = default;
            if (!m_samples.TryGetValue(targetId, out Queue<CombatLagSample> queue) ||
                queue.Count == 0)
            {
                NotReady++;
                if (m_telemetry != null) m_telemetry.LagCompNotReady++;
                return false;
            }
            bool found = false;
            foreach (CombatLagSample sample in queue)
            {
                if (sample.Tick > tick)
                {
                    if (!found)
                    {
                        NotReady++;
                        if (m_telemetry != null) m_telemetry.LagCompNotReady++;
                        return false;
                    }
                    break;
                }
                position = sample.Position;
                velocity = sample.Velocity;
                found = true;
            }
            if (!found)
            {
                NotReady++;
                if (m_telemetry != null) m_telemetry.LagCompNotReady++;
                return false;
            }
            Sample++;
            if (m_telemetry != null) m_telemetry.LagCompSample++;
            return true;
        }

        public void Clear()
        {
            m_samples.Clear();
            Sample = 0L;
            BufferFull = 0L;
            NotReady = 0L;
            if (m_telemetry != null)
            {
                m_telemetry.LagCompSample = 0L;
                m_telemetry.LagCompBufferFull = 0L;
                m_telemetry.LagCompNotReady = 0L;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SuMultiplayer.Net;

using Google.Protobuf;
using PA = SuMultiplayer.Protocol.Authority;

namespace SuMultiplayer.Terrain
{
    /// <summary>
    /// "客户端只是副本"的纯策略层（**不依赖引擎**，P:\tmp\tsverify 直接编译本文件做真实 TCP 自检）。
    ///
    /// 架构决定：**主机是唯一权威，客户端是副本**。
    ///   · 客户端不跑任何会改地形的世界模拟（流体/火/天气/生长/爆炸……）；
    ///   · 客户端对地形的写入只有两个来源：主机 <c>CellChange</c> / <c>ChunkResync</c>，
    ///     以及"玩家编辑的请求回执"（主机批准后同样经 CellChange 到达）；
    ///   · 玩家编辑先由客户端捕获为 <see cref="ClientEditRequest"/> 发给主机，
    ///     主机校验（区块已加载 / 距离 / 方块合法性 / 权限）后走自己的权威路径落地并广播；
    ///     被拒绝或超时就回到主机最后的值（拒绝回执携带权威值，超时走强制核对）。
    ///
    /// 为什么这些逻辑放在 Terrain/ 而不是 Engine/：它们全是纯策略（没有 Game 类型），
    /// 放在这里就能与 TerrainSync 用同一份真实源码跑离线自检，不会出现"测试里复制一份实现"。
    /// </summary>
    public enum ReplicaMutationSource : byte
    {
        Fluid = 0,
        Magma = 1,
        Fire = 2,
        Weather = 3,
        Grass = 4,
        Plant = 5,
        DeciduousLeaves = 6,
        Cactus = 7,
        Rot = 8,
        Carpet = 9,
        FallenLeaves = 10,
        Soil = 11,
        Ivy = 12,
        Sapling = 13,
        Explosion = 14,
        CreatureSpawn = 15,
        Spawn = 16,
        Electricity = 17,
        Unknown = 18
    }

    public static class ReplicaMutationSourceNames
    {
        private static readonly string[] s_names =
        {
            "fluid", "magma", "fire", "weather", "grass", "plant", "deciduous-leaves",
            "cactus", "rot", "carpet", "fallen-leaves", "soil", "ivy", "sapling",
            "explosion", "creature-spawn", "spawn", "electricity", "unknown"
        };

        public static string Of(ReplicaMutationSource source)
        {
            int index = (int)source;
            return index >= 0 && index < s_names.Length ? s_names[index] : "unknown";
        }

        public static int Count => s_names.Length;
    }

    /// <summary>
    /// 客户端世界模拟闸门：**唯一判定入口**（引擎侧每个会改地形的更新都问它）。
    /// 主机 / 单机 = 恒允许；客户端副本 = 恒拒绝并计数（每类一个计数器，可审计、可限流打印）。
    /// 实例状态挂在注入后的 <c>SuSubsystemTerrain</c> 上（**没有静态字段**）。
    /// </summary>
    public sealed class ReplicaSimulationGate
    {
        private readonly long[] m_suppressed = new long[ReplicaMutationSourceNames.Count];
        private long m_allowed;
        private long m_denied;

        /// <summary>本端是不是"联机客户端副本"。由模块在角色确定后每帧写入。</summary>
        public bool ClientReplica { get; set; }

        public long Allowed => m_allowed;
        public long Denied => m_denied;
        public bool AnythingDenied => m_denied > 0;

        /// <summary>引擎侧更新在**改任何格子之前**调用；返回 false = 本帧不要推进模拟。</summary>
        public bool Allow(ReplicaMutationSource source)
        {
            if (!ClientReplica)
            {
                m_allowed++;
                return true;
            }
            int index = (int)source;
            if (index >= 0 && index < m_suppressed.Length) m_suppressed[index]++;
            m_denied++;
            return false;
        }

        public long Suppressed(ReplicaMutationSource source)
        {
            int index = (int)source;
            return index >= 0 && index < m_suppressed.Length ? m_suppressed[index] : 0;
        }

        public void Reset()
        {
            Array.Clear(m_suppressed, 0, m_suppressed.Length);
            m_allowed = 0;
            m_denied = 0;
        }

        /// <summary>可审计的一行（只列非零项；没有否决就是 "none"）。</summary>
        public string Describe()
        {
            var builder = new StringBuilder(96);
            builder.Append("replica=").Append(ClientReplica ? "client" : "authority");
            builder.Append(" allowed=").Append(m_allowed.ToString(CultureInfo.InvariantCulture));
            builder.Append(" denied=").Append(m_denied.ToString(CultureInfo.InvariantCulture));
            builder.Append(" [");
            bool any = false;
            for (int i = 0; i < m_suppressed.Length; i++)
            {
                if (m_suppressed[i] == 0) continue;
                if (any) builder.Append(',');
                any = true;
                builder.Append(ReplicaMutationSourceNames.Of((ReplicaMutationSource)i));
                builder.Append('=').Append(m_suppressed[i].ToString(CultureInfo.InvariantCulture));
            }
            if (!any) builder.Append("none");
            builder.Append(']');
            return builder.ToString();
        }
    }

    /// <summary>客户端捕获到的一次本地地形写入（contents + data，光照不算）。</summary>
    public readonly struct ClientCellEdit
    {
        public ClientCellEdit(int x, int y, int z, int contents, int data)
        {
            X = x; Y = y; Z = z; Contents = contents; Data = data;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public int Contents { get; }
        public int Data { get; }
        public ChunkKey Key => new ChunkKey(X >> 4, Z >> 4);
    }

    /// <summary>
    /// 一条待确认的客户端编辑请求（客户端侧）。
    /// 序号 <see cref="Sequence"/> 每条连接从 1 开始单调递增；主机按 (clientId, seq) 去重。
    /// </summary>
    public sealed class ClientEditRequest
    {
        public long Sequence;
        public int X;
        public int Y;
        public int Z;
        public int Contents;
        public int Data;
        /// <summary>第一次发送的时刻（毫秒，调用方时钟口径）。</summary>
        public long FirstSentMs;
        /// <summary>最近一次发送的时刻（毫秒）。</summary>
        public long LastSentMs;
        /// <summary>已经发送过几次（含第一次）。</summary>
        public int Attempts;
        public ChunkKey Key => new ChunkKey(X >> 4, Z >> 4);
    }

    /// <summary>
    /// 客户端编辑请求的待确认表：
    ///   · <see cref="Begin"/> 登记一次捕获（有界；超过上限 = 明确计数 + 拒绝登记，不静默丢）；
    ///   · <see cref="CollectDue"/> 按重发间隔给出要重发的请求（丢包 / 回执丢失都能自愈）；
    ///   · <see cref="CollectExpired"/> 超过总时限的请求（调用方负责回滚 + 强制核对 + 写日志）；
    ///   · <see cref="Confirm"/> / <see cref="Reject"/> 由主机回执驱动。
    /// </summary>
    public sealed class ClientEditTracker
    {
        /// <summary>同时在途的上限（正常玩家操作远低于它；超出是异常，必须计数）。</summary>
        public const int MaxPending = 256;

        private readonly Dictionary<long, ClientEditRequest> m_pending =
            new Dictionary<long, ClientEditRequest>();
        private readonly List<ClientEditRequest> m_due = new List<ClientEditRequest>();
        private readonly List<ClientEditRequest> m_expired = new List<ClientEditRequest>();
        private long m_nextSequence = 1;

        /// <summary>重发间隔（毫秒）：250ms 对局域网足够，又不至于把通道灌满。</summary>
        public int ResendIntervalMs { get; set; } = 250;

        /// <summary>总时限（毫秒）：超过就回滚并强制核对（默认 5s）。</summary>
        public int TimeoutMs { get; set; } = 5000;

        /// <summary>同一请求最多发送几次（含第一次）；到达上限后按超时处理。</summary>
        public int MaxAttempts { get; set; } = 12;

        public long Submitted { get; private set; }
        public long Confirmed { get; private set; }
        public long Rejected { get; private set; }
        public long Expired { get; private set; }
        public long Resent { get; private set; }
        public long Overflow { get; private set; }
        public long UnknownResult { get; private set; }

        public int PendingCount => m_pending.Count;
        public long NextSequence => m_nextSequence;

        /// <summary>下一次分配出去的序号（测试用：可读回实际发出去的 seq）。</summary>
        public long LastAllocatedSequence { get; private set; }

        /// <summary>登记一次本地编辑；返回 null = 超过上限（<see cref="Overflow"/> 已计数）。</summary>
        public ClientEditRequest Begin(int x, int y, int z, int contents, int data, long nowMs)
        {
            if (m_pending.Count >= MaxPending)
            {
                Overflow++;
                return null;
            }
            long sequence = m_nextSequence;
            m_nextSequence = sequence == long.MaxValue ? 1L : sequence + 1L;
            var request = new ClientEditRequest
            {
                Sequence = sequence,
                X = x,
                Y = y,
                Z = z,
                Contents = contents,
                Data = data,
                FirstSentMs = nowMs,
                LastSentMs = nowMs,
                Attempts = 1
            };
            m_pending[sequence] = request;
            LastAllocatedSequence = sequence;
            Submitted++;
            return request;
        }

        /// <summary>到点要重发的请求（只读列表，复用内部缓冲；调用方立刻消费）。</summary>
        public IReadOnlyList<ClientEditRequest> CollectDue(long nowMs)
        {
            m_due.Clear();
            if (m_pending.Count == 0) return m_due;
            foreach (ClientEditRequest request in m_pending.Values)
            {
                if (nowMs - request.FirstSentMs >= TimeoutMs) continue;      // 交给 CollectExpired
                if (request.Attempts >= MaxAttempts) continue;
                if (nowMs - request.LastSentMs < ResendIntervalMs) continue;
                request.LastSentMs = nowMs;
                request.Attempts++;
                Resent++;
                m_due.Add(request);
            }
            return m_due;
        }

        /// <summary>超过总时限 / 重发次数用尽的请求：从表里移除并返回（调用方负责回滚）。</summary>
        public IReadOnlyList<ClientEditRequest> CollectExpired(long nowMs)
        {
            m_expired.Clear();
            if (m_pending.Count == 0) return m_expired;
            List<long> remove = null;
            foreach (ClientEditRequest request in m_pending.Values)
            {
                bool timeout = nowMs - request.FirstSentMs >= TimeoutMs;
                bool exhausted = request.Attempts >= MaxAttempts &&
                    nowMs - request.FirstSentMs >= ResendIntervalMs;
                if (!timeout && !exhausted) continue;
                (remove ?? (remove = new List<long>(4))).Add(request.Sequence);
            }
            if (remove == null) return m_expired;
            for (int i = 0; i < remove.Count; i++)
            {
                if (!m_pending.TryGetValue(remove[i], out ClientEditRequest request)) continue;
                m_pending.Remove(remove[i]);
                m_expired.Add(request);
                Expired++;
            }
            return m_expired;
        }

        /// <summary>主机批准（或主机回执说"值已经等于请求值"）：移除该请求。</summary>
        public bool Confirm(long sequence)
        {
            if (!m_pending.Remove(sequence)) return false;
            Confirmed++;
            return true;
        }

        /// <summary>主机明确拒绝：移除并计数（回滚由调用方用回执里的权威值执行）。</summary>
        public bool Reject(long sequence)
        {
            if (!m_pending.Remove(sequence)) return false;
            Rejected++;
            return true;
        }

        /// <summary>主机回执的 seq 不在表里（重复回执 / 已超时 / 伪造）：计数，不猜这是哪一格。</summary>
        public void NoteUnknownResult(long sequence)
        {
            _ = sequence;
            UnknownResult++;
        }

        public void Clear()
        {
            m_pending.Clear();
            m_due.Clear();
            m_expired.Clear();
        }

        public string Describe()
        {
            return "submitted=" + Submitted.ToString(CultureInfo.InvariantCulture) +
                " confirmed=" + Confirmed.ToString(CultureInfo.InvariantCulture) +
                " rejected=" + Rejected.ToString(CultureInfo.InvariantCulture) +
                " expired=" + Expired.ToString(CultureInfo.InvariantCulture) +
                " resent=" + Resent.ToString(CultureInfo.InvariantCulture) +
                " overflow=" + Overflow.ToString(CultureInfo.InvariantCulture) +
                " unknown=" + UnknownResult.ToString(CultureInfo.InvariantCulture) +
                " pending=" + m_pending.Count.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// CellEditRequest（客户端 → 主机，proto：Protocol/authority.proto）。
    /// sequence 必须 > 0；重复序号由主机按上一次裁决幂等返回。
    /// </summary>
    public static class CellEditRequestCodec
    {
        public static byte[] Encode(long sequence, int x, int y, int z, int contents, int data)
        {
            var message = new PA.CellEditRequest
            {
                Sequence = sequence,
                X = x,
                Y = y,
                Z = z,
                Contents = contents,
                Data = data
            };
            return message.ToByteArray();
        }

        public static byte[] Encode(ClientCellEdit edit, long sequence) =>
            Encode(sequence, edit.X, edit.Y, edit.Z, edit.Contents, edit.Data);

        public static bool TryDecode(byte[] payload, out long sequence, out int x, out int y, out int z,
            out int contents, out int data)
        {
            sequence = 0; x = 0; y = 0; z = 0; contents = 0; data = 0;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PA.CellEditRequest message = PA.CellEditRequest.Parser.ParseFrom(payload);
                sequence = message.Sequence;
                x = message.X;
                y = message.Y;
                z = message.Z;
                contents = message.Contents;
                data = message.Data;
                return sequence > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static class CellEditResultCodec
    {
        public static byte[] Encode(long sequence, bool accepted, int x, int y, int z,
            int contents, int data, string reason, bool hasValue = true)
        {
            var message = new PA.CellEditResult
            {
                Sequence = sequence,
                Accepted = accepted,
                X = x,
                Y = y,
                Z = z,
                Contents = contents,
                Data = data,
                Reason = reason ?? string.Empty,
                HasValue = hasValue
            };
            return message.ToByteArray();
        }

        public static bool TryDecode(byte[] payload, out long sequence, out bool accepted,
            out int x, out int y, out int z, out int contents, out int data, out string reason,
            out bool hasValue)
        {
            sequence = 0; accepted = false; x = 0; y = 0; z = 0; contents = 0; data = 0;
            reason = string.Empty;
            hasValue = true;
            if (payload == null || payload.Length == 0) return false;
            try
            {
                PA.CellEditResult message = PA.CellEditResult.Parser.ParseFrom(payload);
                sequence = message.Sequence;
                accepted = message.Accepted;
                x = message.X;
                y = message.Y;
                z = message.Z;
                contents = message.Contents;
                data = message.Data;
                reason = message.Reason ?? string.Empty;
                hasValue = message.HasValue;
                return sequence > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>主机侧校验 / 落地要问世界的事实（测试用合成世界实现；引擎侧由 TerrainBridge 实现）。</summary>
    public interface IEditAuthorityWorld
    {
        /// <summary>这一块的内容是否可用（主机已加载）。</summary>
        bool HasContents(ChunkKey key);
        /// <summary>读当前值（contents + data）。</summary>
        bool TryReadCell(int x, int y, int z, out int contents, out int data);
        /// <summary>按主机的权威路径落地（引擎里就是 SubsystemTerrain.ChangeCell 那条路）。</summary>
        bool TryApplyCell(int x, int y, int z, int contents, int data,
            out int resultContents, out int resultData, out string reason);
    }

    /// <summary>主机对一条编辑请求的裁决。</summary>
    public sealed class ClientEditDecision
    {
        public long Sequence;
        public bool Accepted;
        public int X;
        public int Y;
        public int Z;
        /// <summary>决策后该格的权威 value（拒绝时 = 客户端应回到的值）。</summary>
        public int Contents;
        public int Data;
        public string Reason = string.Empty;
        /// <summary>true = 重复序号，原样重发上一次的裁决（幂等，不重复落地）。</summary>
        public bool Duplicate;
        /// <summary>
        /// <see cref="Contents"/>/<see cref="Data"/> 是否是**读到的权威值**。
        /// false（区块不在主机内存 / 坐标越界读不到）= 客户端不能用 0/0 回滚，必须走强制核对。
        /// </summary>
        public bool HasValue;
    }

    /// <summary>
    /// 主机侧编辑裁决器（纯策略）：**唯一入口**，负责
    ///   ① 解析载荷（畸形 = 明确拒绝，不猜）；
    ///   ② 每连接序号去重 / 重放安全（重复序号返回上一次裁决，不重复落地）；
    ///   ③ 校验：区块已加载、距离（相对该客户端兴趣中心）、方块合法性、y 范围、只收玩家端；
    ///   ④ 落地（交给世界实现的权威路径），并把决策后的值回给调用方。
    ///
    /// 主机把改动广播给**所有**客户端是既有 CellChange 广播链的职责：这里落地后，
    /// 主机引擎的 m_modifiedCells 会捕获它并由 TerrainSyncHost.BroadcastCellChange 下发。
    /// </summary>
    public sealed class ClientEditAuthority
    {
        private sealed class ClientState
        {
            public long LastSequence;
            public ClientEditDecision LastDecision;
        }

        private readonly IEditAuthorityWorld m_world;
        private readonly Dictionary<int, ClientState> m_clients = new Dictionary<int, ClientState>();

        /// <summary>方块索引上限（= BlocksManager.Blocks.Length；由引擎侧注入）。</summary>
        public int BlockCount { get; set; } = 1024;

        /// <summary>距离上限（格）：客户端只能改自己兴趣窗口内的格子，外加这个边距。</summary>
        public int MaxDistanceMargin { get; set; } = 16;

        /// <summary>允许的最大序号跳跃（防伪造序号把去重状态推到未来）。</summary>
        public long MaxSequenceJump { get; set; } = 4096;

        public long Requests { get; private set; }
        public long Accepted { get; private set; }
        public long Rejected { get; private set; }
        public long Duplicates { get; private set; }
        public long Malformed { get; private set; }
        public long OutOfRange { get; private set; }
        public long NotPlayer { get; private set; }
        public long ApplyFailures { get; private set; }
        public long ChunkNotLoaded { get; private set; }

        public ClientEditAuthority(IEditAuthorityWorld world)
        {
            m_world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>连接建立 / 断开时调用（序号按**连接**归档；重连是新 clientId，序号从头开始）。</summary>
        public void ResetClient(int clientId)
        {
            m_clients.Remove(clientId);
        }

        public void Clear()
        {
            m_clients.Clear();
        }

        /// <summary>
        /// 处理一条原始载荷。
        /// <paramref name="isPlayerPeer"/> = false（调试端）一律拒绝；
        /// <paramref name="hasRange"/> = false（还没上报兴趣中心）也拒绝 —— 没有距离依据就不批。
        /// </summary>
        public ClientEditDecision Handle(int clientId, byte[] payload, bool isPlayerPeer,
            bool hasRange, int centerCellX, int centerCellZ, int radiusChunks)
        {
            if (!CellEditRequestCodec.TryDecode(payload, out long sequence, out int x, out int y,
                out int z, out int contents, out int data))
            {
                Requests++;
                Malformed++;
                var malformed = new ClientEditDecision
                {
                    Reason = "载荷非法（protobuf 解析失败 / sequence 非正）"
                };
                Rejected++;
                return malformed;
            }
            return Handle(clientId, new PA.CellEditRequest
            {
                Sequence = sequence, X = x, Y = y, Z = z, Contents = contents, Data = data
            }, isPlayerPeer, hasRange, centerCellX, centerCellZ, radiusChunks);
        }

        /// <summary>强类型入口（MessageRegistry 分发用）：不再"解析→再序列化→再解析"。</summary>
        public ClientEditDecision Handle(int clientId, PA.CellEditRequest request, bool isPlayerPeer,
            bool hasRange, int centerCellX, int centerCellZ, int radiusChunks)
        {
            Requests++;
            var decision = new ClientEditDecision();
            if (!isPlayerPeer)
            {
                NotPlayer++;
                decision.Reason = "不是玩家端（调试端只读）";
                Rejected++;
                return decision;
            }
            long sequence = request?.Sequence ?? 0;
            int x = request?.X ?? 0, y = request?.Y ?? 0, z = request?.Z ?? 0;
            int contents = request?.Contents ?? 0, data = request?.Data ?? 0;
            if (sequence <= 0)
            {
                Malformed++;
                decision.Reason = "载荷非法（sequence 必须 > 0）";
                Rejected++;
                return decision;
            }
            decision.Sequence = sequence;
            decision.X = x;
            decision.Y = y;
            decision.Z = z;
            if (!m_clients.TryGetValue(clientId, out ClientState state))
            {
                state = new ClientState();
                m_clients[clientId] = state;
            }
            if (sequence <= state.LastSequence)
            {
                if (sequence == state.LastSequence && state.LastDecision != null)
                {
                    Duplicates++;
                    // 幂等重放：原样返回上一次裁决（客户端重发 / 回执丢失都能收敛）。
                    return state.LastDecision;
                }
                Rejected++;
                decision.Reason = "序号过期（已处理到 " + state.LastSequence.ToString(CultureInfo.InvariantCulture) + "）";
                state.LastDecision = decision;
                return decision;
            }
            if (sequence - state.LastSequence > MaxSequenceJump)
            {
                Rejected++;
                decision.Reason = "序号跳跃过大（+" + (sequence - state.LastSequence).ToString(CultureInfo.InvariantCulture) +
                    "，上限 " + MaxSequenceJump.ToString(CultureInfo.InvariantCulture) + "）";
                state.LastSequence = sequence;
                state.LastDecision = decision;
                return decision;
            }
            state.LastSequence = sequence;

            if (y <= 0 || y >= 255)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "y 越界（只允许 1..254）：" + y.ToString(CultureInfo.InvariantCulture);
            }
            else if (contents < 0 || contents >= BlockCount)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "方块索引非法：" + contents.ToString(CultureInfo.InvariantCulture) +
                    "（0.." + (BlockCount - 1).ToString(CultureInfo.InvariantCulture) + "）";
            }
            else if (data < 0 || data > 65535)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "方块 data 越界：" + data.ToString(CultureInfo.InvariantCulture);
            }
            else if (!hasRange)
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "该客户端还没有上报兴趣中心 → 没有距离依据，不批准编辑";
            }
            else if (!InRange(x, z, centerCellX, centerCellZ, radiusChunks))
            {
                Rejected++;
                OutOfRange++;
                decision.Reason = "距离过远（格 " + x.ToString(CultureInfo.InvariantCulture) + "," +
                    z.ToString(CultureInfo.InvariantCulture) + " 不在兴趣窗口中心 " +
                    centerCellX.ToString(CultureInfo.InvariantCulture) + "," +
                    centerCellZ.ToString(CultureInfo.InvariantCulture) + " 半径 " +
                    radiusChunks.ToString(CultureInfo.InvariantCulture) + " 内）";
            }
            else
            {
                var key = new ChunkKey(x >> 4, z >> 4);
                if (!m_world.HasContents(key))
                {
                    Rejected++;
                    ChunkNotLoaded++;
                    decision.Reason = "区块未加载：" + key.X.ToString(CultureInfo.InvariantCulture) + "," +
                        key.Z.ToString(CultureInfo.InvariantCulture);
                }
                else if (!m_world.TryReadCell(x, y, z, out int currentContents, out int currentData))
                {
                    Rejected++;
                    ChunkNotLoaded++;
                    decision.Reason = "读不到该格当前值（区块不在内存）";
                }
                else if (currentContents == contents && currentData == data)
                {
                    // 已经是目标值：批准（幂等），权威值就是当前值。
                    Accepted++;
                    decision.Accepted = true;
                    decision.HasValue = true;
                    decision.Contents = currentContents;
                    decision.Data = currentData;
                    decision.Reason = "already";
                }
                else if (!m_world.TryApplyCell(x, y, z, contents, data,
                    out int resultContents, out int resultData, out string applyReason))
                {
                    Rejected++;
                    ApplyFailures++;
                    decision.Reason = "主机落地失败：" + (applyReason ?? "unknown");
                    if (m_world.TryReadCell(x, y, z, out int afterContents, out int afterData))
                    {
                        decision.HasValue = true;
                        decision.Contents = afterContents;
                        decision.Data = afterData;
                    }
                }
                else
                {
                    Accepted++;
                    decision.Accepted = true;
                    decision.HasValue = true;
                    decision.Contents = resultContents;
                    decision.Data = resultData;
                    decision.Reason = "ok";
                }
            }
            if (!decision.Accepted && !decision.HasValue &&
                m_world.TryReadCell(x, y, z, out int rejectedContents, out int rejectedData))
            {
                // 拒绝也要带**主机当前权威值**：客户端的回滚目标就是它（否则只能等整块回传）。
                decision.HasValue = true;
                decision.Contents = rejectedContents;
                decision.Data = rejectedData;
            }
            state.LastDecision = decision;
            return decision;
        }

        private bool InRange(int x, int z, int centerCellX, int centerCellZ, int radiusChunks)
        {
            int limit = Math.Max(1, radiusChunks) * 16 + MaxDistanceMargin;
            return Math.Abs(x - centerCellX) <= limit && Math.Abs(z - centerCellZ) <= limit;
        }

        public string Describe()
        {
            return "requests=" + Requests.ToString(CultureInfo.InvariantCulture) +
                " accepted=" + Accepted.ToString(CultureInfo.InvariantCulture) +
                " rejected=" + Rejected.ToString(CultureInfo.InvariantCulture) +
                " duplicates=" + Duplicates.ToString(CultureInfo.InvariantCulture) +
                " malformed=" + Malformed.ToString(CultureInfo.InvariantCulture) +
                " outOfRange=" + OutOfRange.ToString(CultureInfo.InvariantCulture) +
                " notPlayer=" + NotPlayer.ToString(CultureInfo.InvariantCulture) +
                " chunkNotLoaded=" + ChunkNotLoaded.ToString(CultureInfo.InvariantCulture) +
                " applyFailures=" + ApplyFailures.ToString(CultureInfo.InvariantCulture);
        }
    }
}

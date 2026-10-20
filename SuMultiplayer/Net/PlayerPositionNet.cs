using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using SuMultiplayer.Players;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 一份**只读位置/姿态**快照。
    ///
    /// Source: Mod/ScMultiplayer/Core/PlayerReadOnlyStateSnapshot.cs:10-31
    ///   旧实现刻意把位置/姿态摘成一个**只含位置/姿态、不含背包/输入/动作/权威结果**的边界
    ///   （"Position/pose data is a replaceable read-only state boundary. It contains no inventory,
    ///    input, action or authority result"）。位置同步消息的读段就落在这层上。
    /// Source: Mod/ScMultiplayer/Message/GamePlayerPositionMessage.cs:78-160
    ///   旧 <c>GamePlayerPositionMessage</c> 的线格式。本结构携带它的位置/姿态字段，
    ///   字段顺序与旧 <c>WritePayload</c>/<c>ReadPayload</c> **一致**：
    ///     PlayerIndex → ServerTick → Position → Rotation → Velocity → LookAngles
    ///     → … → IsCrouching → IsFlying → IsGrounded
    ///   旧消息尾部的 PokingPhase/AttackOrder/RowLeftOrder/RowRightOrder/MountEntityId/
    ///   ActiveSlotIndex/HandItem*/ItemOffset/ItemRotation/AimHandAngle/SlotValues/SlotCounts/
    ///   InventoryVersion 分别属于**尚未移植**的挖掘动作、骑乘、手持物与装备/背包链，
    ///   本重写版没有这些链的消费者 —— 见工程报告"未移植"一节（不是静默丢弃：那段结构本身
    ///   也不在旧实现的"只读位置/姿态边界"里）。
    /// </summary>
    public readonly struct PlayerPositionSnapshot
    {
        public PlayerPositionSnapshot(int serverTick, Vector3 position, Quaternion rotation,
            Vector3 velocity, Vector2 lookAngles, bool isCrouching, bool isFlying, bool isGrounded)
        {
            ServerTick = serverTick;
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            LookAngles = lookAngles;
            IsCrouching = isCrouching;
            IsFlying = isFlying;
            IsGrounded = isGrounded;
        }

        /// <summary>发送方自己的位置脉冲序号（旧 <c>client.Step</c>）。只用于丢弃陈旧快照与诊断。</summary>
        public int ServerTick { get; }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public Vector3 Velocity { get; }
        /// <summary>俯仰/偏航（旧 <c>msg.LookAngles</c>；机身 yaw 在 <see cref="Rotation"/> 里）。</summary>
        public Vector2 LookAngles { get; }
        public bool IsCrouching { get; }
        public bool IsFlying { get; }
        public bool IsGrounded { get; }

        /// <summary>
        /// Source: Mod/ScMultiplayer/Core/PlayerReadOnlyStateSnapshot.cs:33-36（IsFinite）
        ///         → 旧 HandleGamePlayerPositionMessage 的 <c>if (!readOnlyState.IsFinite) return;</c>
        /// 非有限值一律**丢弃并写清原因**，绝不写进引擎（NaN 会让物理与地形更新位置一起失效）。
        /// </summary>
        public bool IsFinite =>
            IsFiniteValue(Position.X) && IsFiniteValue(Position.Y) && IsFiniteValue(Position.Z) &&
            IsFiniteValue(Rotation.X) && IsFiniteValue(Rotation.Y) && IsFiniteValue(Rotation.Z) &&
            IsFiniteValue(Rotation.W) &&
            IsFiniteValue(Velocity.X) && IsFiniteValue(Velocity.Y) && IsFiniteValue(Velocity.Z) &&
            IsFiniteValue(LookAngles.X) && IsFiniteValue(LookAngles.Y);

        private static bool IsFiniteValue(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// 批里的一个角色：<see cref="Key"/> 是**玩家键**（本机传输编号 + 本机角色槽位；
    /// 主机自己 = 0:0）。身份绑定仍由 <c>PlayerAvatarModule</c> 的角色记录键负责。
    /// Source: Mod/ScMultiplayer/Message/GamePlayerPositionMessage.cs:10（PlayerIndex = 发送方 ClientID）
    /// Source: Mod/SuMultiplayer/Players/PlayerKey.cs（Phase 1 起追加槽位）
    /// </summary>
    public readonly struct PlayerPositionEntry
    {
        /// <summary>旧版单角色入口：clientId 的槽位 0。</summary>
        public PlayerPositionEntry(int clientId, in PlayerPositionSnapshot snapshot)
            : this(new PlayerKey(clientId, 0), in snapshot)
        {
        }

        public PlayerPositionEntry(PlayerKey key, in PlayerPositionSnapshot snapshot)
        {
            Key = key;
            Snapshot = snapshot;
        }

        public PlayerKey Key { get; }
        /// <summary>传输编号（= <c>Key.ClientId</c>；旧字段）。</summary>
        public int ClientId => Key.ClientId;
        /// <summary>本机角色槽位（= <c>Key.Slot</c>；0 = 旧版单角色）。</summary>
        public int Slot => Key.Slot;
        public PlayerPositionSnapshot Snapshot { get; }
    }

    /// <summary>
    /// 远端角色的"最新一份权威位置" + 插值/平滑状态。
    /// Source: Mod/ScMultiplayer/Core/ScMultiplayerSupportTypes.cs:118-156（NetworkPlayerState 的位置字段）
    /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:71-97
    ///         （HandleGamePlayerPositionMessage 建状态 → readOnlyState.ApplyTo(state) →
    ///           state.ServerTick = msg.ServerTick; state.LastUpdateTime = Time.RealTime）
    /// </summary>
    public sealed class RemotePlayerPositionState
    {
        /// <summary>玩家键（clientId + 本机槽位）；旧字段 <c>ClientId</c> 保留为它的第一段。</summary>
        public PlayerKey Key;
        /// <summary>旧字段（= Key.ClientId）；setter 保留给旧调用方，等价于改键的第一段。</summary>
        public int ClientId
        {
            get => Key.ClientId;
            set => Key = new PlayerKey(value, Key.Slot);
        }
        public int Slot
        {
            get => Key.Slot;
            set => Key = new PlayerKey(Key.ClientId, value);
        }

        /// <summary>最近一份权威快照（旧 <c>state.Position/Rotation/Velocity/LookAngles/Is*</c>）。</summary>
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.Identity;
        public Vector3 Velocity;
        public Vector2 LookAngles;
        public bool IsCrouching;
        public bool IsFlying;
        public bool IsGrounded;

        /// <summary>最近一次已应用的快照序号（旧 <c>state.ServerTick</c>，用于丢弃迟到/陈旧快照）。</summary>
        public int ServerTick = -1;

        /// <summary>
        /// 旧 <c>state.EstimatedDelay</c>：外推时间（上限 <c>RemoteExtrapolationLimit = 0.15f</c>）。
        /// 旧实现用**共享网络步**差值估它（<c>(client.Step - state.ServerTick) * ServerTickDuration</c>，
        /// 见 ScMultiplayerMessageHandlers.cs:180-184）；本重写版的 TCP 通道没有共享步计数器，
        /// 所以由接收侧用**相邻快照的到达间隔**估（旧实现自己的动物表现辅助用的就是这个量，
        /// 见 ScMultiplayerMessageHandlers.cs:1194-1204 的 arrivalAge），平滑系数与旧实现相同（0.12）。
        /// </summary>
        public float EstimatedDelay;

        /// <summary>首帧直接对齐（旧 <c>state.PresentationInitialized</c>）。</summary>
        public bool PresentationInitialized;

        /// <summary>最后一次收到快照的时刻（旧 <c>state.LastUpdateTime</c>，Time.RealTime 口径）。</summary>
        public double LastUpdateTime;

        /// <summary>
        /// Source: Mod/ScMultiplayer/Core/PlayerReadOnlyStateSnapshot.cs:37-48（ApplyTo）
        /// Source: Mod/ScMultiplayer/Modules/Network/ScMultiplayerMessageHandlers.cs:78-97
        /// 只更新"最新权威快照"本身；**不动引擎里的任何对象**（应用在表现辅助里做）。
        /// </summary>
        public void Apply(in PlayerPositionSnapshot snapshot, float arrivalAge, double now)
        {
            Position = snapshot.Position;
            Rotation = snapshot.Rotation;
            Velocity = snapshot.Velocity;
            LookAngles = snapshot.LookAngles;
            IsCrouching = snapshot.IsCrouching;
            IsFlying = snapshot.IsFlying;
            IsGrounded = snapshot.IsGrounded;
            ServerTick = snapshot.ServerTick;
            float delaySample = MathUtils.Clamp(arrivalAge, 0f, RemoteDelaySampleLimit);
            EstimatedDelay = EstimatedDelay <= 0f
                ? delaySample
                : MathUtils.Lerp(EstimatedDelay, delaySample, 0.12f);
            LastUpdateTime = now;
        }

        /// <summary>旧 <c>RemoteDelaySampleLimit = 0.6f</c>（Core/ScMultiplayerRuntimeState.cs:827）。</summary>
        public const float RemoteDelaySampleLimit = 0.6f;

        /// <summary>旧 <c>RemoteExtrapolationLimit = 0.15f</c>（Core/ScMultiplayerRuntimeState.cs:831）。</summary>
        public const float RemoteExtrapolationLimit = 0.15f;

        /// <summary>旧 <c>RemotePresentationStaleTime = 2f</c>（Core/ScMultiplayerRuntimeState.cs:832）。</summary>
        public const float RemotePresentationStaleTime = 2f;
    }

    /// <summary>
    /// 位置快照的编解码（proto：Protocol/position.proto）。
    ///
    ///   · 单条 = <c>PlayerPositions{ entries: [1] }</c>；批 = <c>PlayerPositions{ entries: [N] }</c>。
    ///   · 批上限：旧 <c>GamePlayerPositionsMessage</c> 是 16；多本机角色以后主机侧最多
    ///     4 个本机角色 + 4 个玩家端 × 4 个本机角色 = 20 条，所以上限提到
    ///     <see cref="MaxBatchEntries"/> = 24。旧客户端收到的批会被主机过滤成
    ///     "每个连接只有槽位 0"（≤5 条），因此 16 条上限的旧客户端不受影响。
    ///   · 向量/四元数用 protobuf 的 <c>Vec3/Quat/Vec2</c> 子消息，值域与旧的
    ///     <c>WriteVector3/WriteQuaternion/WriteVector2</c> 完全一致（3/4/2 个 float）。
    ///   · 解码失败（缺 position/rotation/velocity/look_angles、批超限、槽位非法、protobuf 非法）
    ///     **必须显式失败**：返回 false + 原因，调用方丢弃并写日志，绝不用默认值顶替。
    /// </summary>
    public static class PlayerPositionCodec
    {
        /// <summary>
        /// 批上限。旧版 16；多本机角色需要 4（本机）+ 4 客户端 × 4 = 20。
        /// </summary>
        public const int MaxBatchEntries = 24;

        /// <summary>客户端 → 主机：单条快照（旧 GamePlayerPositionMessage；槽位 0）。</summary>
        public static byte[] EncodeSingle(int clientId, in PlayerPositionSnapshot snapshot) =>
            EncodeSingle(new PlayerKey(clientId, 0), in snapshot);

        /// <summary>客户端 → 主机：单条快照（多本机角色：<paramref name="key"/> 的槽位一起写）。</summary>
        public static byte[] EncodeSingle(PlayerKey key, in PlayerPositionSnapshot snapshot)
        {
            var message = new PP.PlayerPositions();
            message.Entries.Add(ToMessage(key, in snapshot));
            return message.ToByteArray();
        }

        /// <summary>主机 → 客户端：一批快照（旧 GamePlayerPositionsMessage）。</summary>
        public static byte[] EncodeBatch(IReadOnlyList<PlayerPositionEntry> entries, out int dropped)
        {
            dropped = 0;
            int count = entries?.Count ?? 0;
            if (count > MaxBatchEntries)
            {
                // 显式：超限的条目**不静默截断后假装全发**，而是报出被丢弃的数量（调用方写日志）。
                dropped = count - MaxBatchEntries;
                count = MaxBatchEntries;
            }
            var message = new PP.PlayerPositions();
            for (int i = 0; i < count; i++)
            {
                PlayerPositionEntry entry = entries[i];
                PlayerPositionSnapshot snapshot = entry.Snapshot;
                message.Entries.Add(ToMessage(entry.Key, in snapshot));
            }
            return message.ToByteArray();
        }

        /// <summary>
        /// 旧版单角色解码：只接受槽位 0（槽位 &gt; 0 返回 false + 明确原因，
        /// 需要槽位的调用方用 <see cref="TryDecodeSingle(byte[], out PlayerKey, out PlayerPositionSnapshot, out string)"/>）。
        /// </summary>
        public static bool TryDecodeSingle(byte[] payload, out int clientId,
            out PlayerPositionSnapshot snapshot, out string error)
        {
            clientId = 0;
            if (!TryDecodeSingle(payload, out PlayerKey key, out snapshot, out error)) return false;
            if (key.Slot != 0)
            {
                error = "单条快照的槽位是 " + key.Slot + "（旧接口只支持槽位 0）";
                return false;
            }
            clientId = key.ClientId;
            return true;
        }

        /// <summary>单条解码（带玩家键）。</summary>
        public static bool TryDecodeSingle(byte[] payload, out PlayerKey key,
            out PlayerPositionSnapshot snapshot, out string error)
        {
            key = default;
            snapshot = default;
            error = null;
            if (!TryParse(payload, out PP.PlayerPositions message, out error)) return false;
            if (message.Entries.Count != 1)
            {
                error = "单条位置快照必须恰好 1 条条目（本次 " + message.Entries.Count + " 条）";
                return false;
            }
            return TryReadEntry(message.Entries[0], out key, out snapshot, out error);
        }

        public static bool TryDecodeBatch(byte[] payload, out List<PlayerPositionEntry> entries,
            out string error)
        {
            entries = null;
            error = null;
            if (!TryParse(payload, out PP.PlayerPositions message, out error)) return false;
            if (message.Entries.Count > MaxBatchEntries)
            {
                error = "位置快照批的条数非法：" + message.Entries.Count + "（合法 0.." + MaxBatchEntries + "）";
                return false;
            }
            var result = new List<PlayerPositionEntry>(message.Entries.Count);
            for (int i = 0; i < message.Entries.Count; i++)
            {
                if (!TryReadEntry(message.Entries[i], out PlayerKey key, out PlayerPositionSnapshot item,
                        out error))
                {
                    error = "第 " + (i + 1) + " 条：" + error;
                    return false;
                }
                result.Add(new PlayerPositionEntry(key, item));
            }
            entries = result;
            return true;
        }

        private static bool TryParse(byte[] payload, out PP.PlayerPositions message, out string error)
        {
            message = null;
            error = null;
            if (payload == null || payload.Length == 0)
            {
                error = "载荷为空";
                return false;
            }
            try
            {
                message = PP.PlayerPositions.Parser.ParseFrom(payload);
                return true;
            }
            catch (Exception e)
            {
                error = "解析位置快照失败：" + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        /// <summary>把已解析的条目转成领域快照（旧接口，只接受槽位 0）。</summary>
        public static bool TryConvert(PP.PlayerPosition entry, out int clientId,
            out PlayerPositionSnapshot snapshot, out string error)
        {
            clientId = 0;
            if (!TryReadEntry(entry, out PlayerKey key, out snapshot, out error)) return false;
            if (key.Slot != 0)
            {
                error = "条目槽位是 " + key.Slot + "（旧接口只支持槽位 0）";
                return false;
            }
            clientId = key.ClientId;
            return true;
        }

        /// <summary>把已解析的条目转成领域快照 + 玩家键。</summary>
        public static bool TryConvert(PP.PlayerPosition entry, out PlayerKey key,
            out PlayerPositionSnapshot snapshot, out string error) =>
            TryReadEntry(entry, out key, out snapshot, out error);

        private static bool TryReadEntry(PP.PlayerPosition entry, out PlayerKey key,
            out PlayerPositionSnapshot snapshot, out string error)
        {
            key = default;
            snapshot = default;
            error = null;
            if (entry == null)
            {
                error = "条目为空";
                return false;
            }
            if (!PlayerKey.TryCreate(entry.ClientId, (int)entry.Slot, out key, out error)) return false;
            if (entry.Position == null || entry.Rotation == null || entry.Velocity == null ||
                entry.LookAngles == null)
            {
                error = "条目缺少 position/rotation/velocity/look_angles（协议要求四者都存在）";
                return false;
            }
            snapshot = new PlayerPositionSnapshot(entry.ServerTick,
                new Vector3(entry.Position.X, entry.Position.Y, entry.Position.Z),
                new Quaternion(entry.Rotation.X, entry.Rotation.Y, entry.Rotation.Z, entry.Rotation.W),
                new Vector3(entry.Velocity.X, entry.Velocity.Y, entry.Velocity.Z),
                new Vector2(entry.LookAngles.X, entry.LookAngles.Y),
                entry.IsCrouching, entry.IsFlying, entry.IsGrounded);
            return true;
        }

        private static PP.PlayerPosition ToMessage(PlayerKey key, in PlayerPositionSnapshot s)
        {
            return new PP.PlayerPosition
            {
                ClientId = key.ClientId,
                Slot = (uint)key.Slot,
                ServerTick = s.ServerTick,
                Position = new PP.Vec3 { X = s.Position.X, Y = s.Position.Y, Z = s.Position.Z },
                Rotation = new PP.Quat
                {
                    X = s.Rotation.X, Y = s.Rotation.Y, Z = s.Rotation.Z, W = s.Rotation.W
                },
                Velocity = new PP.Vec3 { X = s.Velocity.X, Y = s.Velocity.Y, Z = s.Velocity.Z },
                LookAngles = new PP.Vec2 { X = s.LookAngles.X, Y = s.LookAngles.Y },
                IsCrouching = s.IsCrouching,
                IsFlying = s.IsFlying,
                IsGrounded = s.IsGrounded
            };
        }
    }
}

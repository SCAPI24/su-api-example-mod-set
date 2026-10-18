using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
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
    /// 批里的一个角色：<paramref name="ClientId"/> 是**本机传输层编号**（主机自己 = 0），
    /// 不是身份 —— 身份绑定仍由 <c>PlayerAvatarModule</c> 的角色记录键负责。
    /// Source: Mod/ScMultiplayer/Message/GamePlayerPositionMessage.cs:10（PlayerIndex = 发送方 ClientID）
    /// </summary>
    public readonly struct PlayerPositionEntry
    {
        public PlayerPositionEntry(int clientId, in PlayerPositionSnapshot snapshot)
        {
            ClientId = clientId;
            Snapshot = snapshot;
        }

        public int ClientId { get; }
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
        public int ClientId;

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
    ///   · 批上限 16 与旧 <c>GamePlayerPositionsMessage</c> 的校验一致（超过 = 协议违规）。
    ///   · 向量/四元数用 protobuf 的 <c>Vec3/Quat/Vec2</c> 子消息，值域与旧的
    ///     <c>WriteVector3/WriteQuaternion/WriteVector2</c> 完全一致（3/4/2 个 float）。
    ///   · 解码失败（缺 position/rotation/velocity/look_angles、批超限、protobuf 非法）
    ///     **必须显式失败**：返回 false + 原因，调用方丢弃并写日志，绝不用默认值顶替。
    /// </summary>
    public static class PlayerPositionCodec
    {
        /// <summary>Source: Mod/ScMultiplayer/Message/GamePlayerPositionsMessage.cs:27-28（count &gt; 16 = 违规）。</summary>
        public const int MaxBatchEntries = 16;

        /// <summary>客户端 → 主机：单条快照（旧 GamePlayerPositionMessage）。</summary>
        public static byte[] EncodeSingle(int clientId, in PlayerPositionSnapshot snapshot)
        {
            var message = new PP.PlayerPositions();
            message.Entries.Add(ToMessage(clientId, in snapshot));
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
                message.Entries.Add(ToMessage(entry.ClientId, in snapshot));
            }
            return message.ToByteArray();
        }

        public static bool TryDecodeSingle(byte[] payload, out int clientId,
            out PlayerPositionSnapshot snapshot, out string error)
        {
            clientId = 0;
            snapshot = default;
            error = null;
            if (!TryParse(payload, out PP.PlayerPositions message, out error)) return false;
            if (message.Entries.Count != 1)
            {
                error = "单条位置快照必须恰好 1 条条目（本次 " + message.Entries.Count + " 条）";
                return false;
            }
            return TryReadEntry(message.Entries[0], out clientId, out snapshot, out error);
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
                if (!TryReadEntry(message.Entries[i], out int clientId, out PlayerPositionSnapshot item,
                        out error))
                {
                    error = "第 " + (i + 1) + " 条：" + error;
                    return false;
                }
                result.Add(new PlayerPositionEntry(clientId, item));
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

        /// <summary>把已解析的 protobuf 条目转成领域快照（强类型处理器用）。</summary>
        public static bool TryConvert(PP.PlayerPosition entry, out int clientId,
            out PlayerPositionSnapshot snapshot, out string error) =>
            TryReadEntry(entry, out clientId, out snapshot, out error);

        private static bool TryReadEntry(PP.PlayerPosition entry, out int clientId,
            out PlayerPositionSnapshot snapshot, out string error)
        {
            clientId = entry?.ClientId ?? 0;
            snapshot = default;
            error = null;
            if (entry == null)
            {
                error = "条目为空";
                return false;
            }
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

        private static PP.PlayerPosition ToMessage(int clientId, in PlayerPositionSnapshot s)
        {
            return new PP.PlayerPosition
            {
                ClientId = clientId,
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

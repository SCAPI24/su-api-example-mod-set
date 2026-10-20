using System;
using System.Collections.Generic;
using Engine;
using Google.Protobuf;
using PE = SuMultiplayer.Protocol.Entities;
using PP = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 投射物开火请求的种类（与 <c>Protocol/entities.proto</c> 的
    /// <c>ProjectileFireKind</c> 数值一一对应）。
    /// Source: Survivalcraft/Game/SubsystemThrowableBlockBehavior.cs:OnAim
    /// Source: Survivalcraft/Game/SubsystemBowBlockBehavior.cs:OnAim
    /// Source: Survivalcraft/Game/SubsystemCrossbowBlockBehavior.cs:OnAim
    /// Source: Survivalcraft/Game/SubsystemMusketBlockBehavior.cs:OnAim
    /// </summary>
    public static class ProjectileFireKinds
    {
        public const int Unspecified = 0;
        public const int Throwable = 1;
        public const int Bow = 2;
        public const int Crossbow = 3;
        public const int Musket = 4;

        public static bool IsKnown(int kind) => kind >= Throwable && kind <= Musket;

        public static string Name(int kind)
        {
            switch (kind)
            {
                case Throwable: return "throwable";
                case Bow: return "bow";
                case Crossbow: return "crossbow";
                case Musket: return "musket";
                default: return "kind-" + kind;
            }
        }
    }

    /// <summary>
    /// 一次客户端丢弃的来源（与 <c>Protocol/entities.proto</c> 的 <c>DropSource</c>
    /// 数值一一对应）。所有**线上/判定层**都只认这两个值，未知值明确拒绝。
    /// </summary>
    public static class DropSources
    {
        /// <summary>键盘 / 手柄 Drop：引擎丢弃**整个活跃槽**（8 m/s × 视线）。</summary>
        public const int Input = 0;

        /// <summary>UI 拖出到世界（ViewWidget.DragDrop）：丢弃指定数量（12 m/s × 拖拽方向）。</summary>
        public const int Ui = 1;

        public static bool IsKnown(int source) => source == Input || source == Ui;

        public static string Name(int source)
        {
            switch (source)
            {
                case Input: return "input";
                case Ui: return "ui";
                default: return "source-" + source;
            }
        }
    }

    /// <summary>
    /// 客户端在**本地扣除之前**采集到的丢弃意图（纯值）。
    ///   · <see cref="DropSources.Input"/>：由 Engine/PickableReplicaEngine.cs 的输入钩子产生，
    ///     先吞掉引擎原生的 Drop 分支（ComponentPlayer.Update 的 RemoveSlotItems + AddPickable）；
    ///   · <see cref="DropSources.Ui"/>：由 Engine/UiDropInterception.cs 在
    ///     <c>ViewWidget.DragDrop</c> 执行之前产生（拖拽仍处于 DragHostWidget 阶段），
    ///     同样不本地扣除、不本地生成拾取物。
    /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
    ///         （position = body + (0, StanceBoxSize.Y*0.66, 0) + 0.25*Forward；
    ///          velocity = 8 * CreateFromQuaternion(EyeRotation).Forward）
    /// Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
    ///         （position = ActiveCamera.ViewPosition；
    ///          velocity = Normalize(ScreenToWorld(drag) - ViewPosition) * 12）
    /// </summary>
    public readonly struct PickableDropIntent
    {
        public PickableDropIntent(int activeSlot, int itemValue, int count, Vector3 position,
            Vector3 direction, int source = DropSources.Input)
        {
            ActiveSlot = activeSlot;
            ItemValue = itemValue;
            Count = count;
            Position = position;
            Direction = direction;
            Source = source;
        }

        public int ActiveSlot { get; }
        public int ItemValue { get; }
        public int Count { get; }
        public Vector3 Position { get; }
        public Vector3 Direction { get; }
        /// <summary><see cref="DropSources"/> 之一。</summary>
        public int Source { get; }

        public string Describe() => "src=" + DropSources.Name(Source) + " slot=" + ActiveSlot +
            " value=" + ItemValue + " count=" + Count + " pos=(" +
            Position.X.ToString("F2") + "," + Position.Y.ToString("F2") + "," +
            Position.Z.ToString("F2") + ")";
    }

    /// <summary>客户端 → 主机：PickableDropRequest 的纯值形态（模块负责序号）。</summary>
    public readonly struct PickableDropRequestData
    {
        public PickableDropRequestData(long sequence, int activeSlot, int itemValue, int count,
            Vector3 position, Vector3 direction, int source = DropSources.Input,
            bool hasReach = false, float reach = 0f)
        {
            Sequence = sequence;
            ActiveSlot = activeSlot;
            ItemValue = itemValue;
            Count = count;
            Position = position;
            Direction = direction;
            Source = source;
            HasReach = hasReach;
            Reach = reach;
        }

        public long Sequence { get; }
        public int ActiveSlot { get; }
        public int ItemValue { get; }
        public int Count { get; }
        public Vector3 Position { get; }
        public Vector3 Direction { get; }
        /// <summary><see cref="DropSources"/> 之一。</summary>
        public int Source { get; }
        /// <summary>请求是否携带了有效 reach（false = 旧客户端 / 旧载荷）。</summary>
        public bool HasReach { get; }
        /// <summary>请求者的有效交互距离（米）；<see cref="HasReach"/> = false 时无意义。</summary>
        public float Reach { get; }

        public string Describe() => "seq=" + Sequence + " src=" + DropSources.Name(Source) +
            " slot=" + ActiveSlot + " value=" + ItemValue + " count=" + Count;
    }

    /// <summary>主机执行一次客户端丢弃请求的结果（成功时构造）。</summary>
    public readonly struct PickableDropOutcome
    {
        public PickableDropOutcome(int itemValue, int count, Vector3 position, Vector3 velocity,
            int[] indices, int[] values, int[] counts, int inventoryVersion,
            int pickableId = 0)
        {
            ItemValue = itemValue;
            Count = count;
            Position = position;
            Velocity = velocity;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
            PickableId = pickableId;
        }

        public int ItemValue { get; }
        public int Count { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }
        /// <summary>主机给这个拾取物分配的实体复制 id（客户端据此立即物化副本）。</summary>
        public int PickableId { get; }

        public bool HasInventory => Indices.Length > 0;
    }

    /// <summary>主机 → 丢弃者：回执 + 稀疏背包增量（其余客户端只收 EntityState(ADD)）。</summary>
    public sealed class PickableDropResultData
    {
        public PickableDropResultData(long sequence, bool accepted, string reason, int itemValue,
            int count, Vector3 position, Vector3 velocity, int[] indices, int[] values,
            int[] counts, int inventoryVersion, int pickableId = 0)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            ItemValue = itemValue;
            Count = count;
            Position = position;
            Velocity = velocity;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
            PickableId = pickableId;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public int ItemValue { get; }
        public int Count { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }
        /// <summary>主机给这个拾取物分配的实体复制 id（0 = 旧主机没有提供）。</summary>
        public int PickableId { get; }

        public bool HasInventory => Indices.Length > 0;

        public PlayerInventoryDelta ToInventoryDelta(int clientId, int revision) =>
            PlayerInventoryDelta.CreateDelta(clientId, revision, InventoryVersion, -1, Indices,
                Values, Counts);

        public string Describe() => "seq=" + Sequence + (Accepted ? " accepted" : " rejected(" +
            Reason + ")") + " value=" + ItemValue + " count=" + Count + " pickable=" +
            PickableId + " delta=" + Indices.Length;
    }

    /// <summary>
    /// 客户端在**本地扣除之前**采集到的投掷 / 开火意图（纯值）。
    /// 由 Engine/ProjectileReplicaBehaviors.cs 的方块行为外壳产生。
    /// </summary>
    public readonly struct ProjectileFireIntent
    {
        public ProjectileFireIntent(int kind, int itemValue, int activeSlot, Vector3 origin,
            Vector3 direction, int charge)
        {
            Kind = kind;
            ItemValue = itemValue;
            ActiveSlot = activeSlot;
            Origin = origin;
            Direction = direction;
            Charge = charge;
        }

        public int Kind { get; }
        public int ItemValue { get; }
        public int ActiveSlot { get; }
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }
        public int Charge { get; }

        public string Describe() => ProjectileFireKinds.Name(Kind) + " value=" + ItemValue +
            " slot=" + ActiveSlot + " charge=" + Charge + " origin=(" +
            Origin.X.ToString("F2") + "," + Origin.Y.ToString("F2") + "," +
            Origin.Z.ToString("F2") + ")";
    }

    /// <summary>客户端 → 主机：ProjectileFireRequest 的纯值形态（模块负责序号）。</summary>
    public readonly struct ProjectileFireRequestData
    {
        public ProjectileFireRequestData(long sequence, int kind, int itemValue, int activeSlot,
            Vector3 origin, Vector3 direction, int charge, bool hasReach = false, float reach = 0f)
        {
            Sequence = sequence;
            Kind = kind;
            ItemValue = itemValue;
            ActiveSlot = activeSlot;
            Origin = origin;
            Direction = direction;
            Charge = charge;
            HasReach = hasReach;
            Reach = reach;
        }

        public long Sequence { get; }
        public int Kind { get; }
        public int ItemValue { get; }
        public int ActiveSlot { get; }
        public Vector3 Origin { get; }
        public Vector3 Direction { get; }
        public int Charge { get; }
        /// <summary>请求是否携带了有效 reach（false = 旧客户端 / 旧载荷）。</summary>
        public bool HasReach { get; }
        /// <summary>请求者的有效交互距离（米）；<see cref="HasReach"/> = false 时无意义。</summary>
        public float Reach { get; }

        public string Describe() => "seq=" + Sequence + " " + ProjectileFireKinds.Name(Kind) +
            " value=" + ItemValue + " slot=" + ActiveSlot + " charge=" + Charge;
    }

    /// <summary>主机执行一次客户端开火请求的结果（成功时构造）。</summary>
    public readonly struct ProjectileFireOutcome
    {
        public ProjectileFireOutcome(int kind, int spawned, int itemValue, Vector3 position,
            Vector3 velocity, int[] indices, int[] values, int[] counts, int inventoryVersion,
            Vector3 origin = default, Vector3 direction = default, bool misfire = false)
        {
            Kind = kind;
            Spawned = spawned;
            ItemValue = itemValue;
            Position = position;
            Velocity = velocity;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
            Origin = origin;
            Direction = direction;
            Misfire = misfire;
        }

        public int Kind { get; }
        public int Spawned { get; }
        public int ItemValue { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }
        /// <summary>主机权威出生点（EyePosition；即使 spawned=0 也有值，供开火表现使用）。</summary>
        public Vector3 Origin { get; }
        /// <summary>主机权威视线方向（EyeRotation 前向）。</summary>
        public Vector3 Direction { get; }
        public bool Misfire { get; }

        public bool HasInventory => Indices.Length > 0;
    }

    /// <summary>主机 → 开火者：回执 + 稀疏背包增量。</summary>
    public sealed class ProjectileFireResultData
    {
        public ProjectileFireResultData(long sequence, bool accepted, string reason, int kind,
            int itemValue, int spawned, Vector3 position, Vector3 velocity, int[] indices,
            int[] values, int[] counts, int inventoryVersion)
        {
            Sequence = sequence;
            Accepted = accepted;
            Reason = reason ?? string.Empty;
            Kind = kind;
            ItemValue = itemValue;
            Spawned = spawned;
            Position = position;
            Velocity = velocity;
            Indices = indices ?? Array.Empty<int>();
            Values = values ?? Array.Empty<int>();
            Counts = counts ?? Array.Empty<int>();
            InventoryVersion = inventoryVersion;
        }

        public long Sequence { get; }
        public bool Accepted { get; }
        public string Reason { get; }
        public int Kind { get; }
        public int ItemValue { get; }
        public int Spawned { get; }
        public Vector3 Position { get; }
        public Vector3 Velocity { get; }
        public int[] Indices { get; }
        public int[] Values { get; }
        public int[] Counts { get; }
        public int InventoryVersion { get; }

        public bool HasInventory => Indices.Length > 0;

        public PlayerInventoryDelta ToInventoryDelta(int clientId, int revision) =>
            PlayerInventoryDelta.CreateDelta(clientId, revision, InventoryVersion, -1, Indices,
                Values, Counts);

        public string Describe() => "seq=" + Sequence + (Accepted
            ? " accepted " + ProjectileFireKinds.Name(Kind) + " spawned=" + Spawned
            : " rejected(" + Reason + ")") + " value=" + ItemValue + " delta=" + Indices.Length;
    }

    /// <summary>
    /// 丢弃 / 开火请求与回执的线上编解码 + 严格校验。
    /// 任何非法载荷都返回 false + 原因（绝不猜默认值 / 半条应用）。
    /// </summary>
    public static class EntityRequestCodec
    {
        public const int MaxActiveSlot = 1024;
        public const int MaxCharge = 15;

        // ------------------------------------------------------------ 丢弃

        public static PE.PickableDropRequest EncodeDrop(in PickableDropRequestData request)
        {
            var message = new PE.PickableDropRequest
            {
                Sequence = request.Sequence,
                ActiveSlot = request.ActiveSlot,
                ItemValue = request.ItemValue,
                Count = request.Count,
                Position = EntityCodec.ToVec3(request.Position),
                Direction = EntityCodec.ToVec3(request.Direction),
                Source = (PE.DropSource)request.Source
            };
            if (request.HasReach) message.Reach = request.Reach;
            return message;
        }

        public static byte[] EncodeDropBytes(in PickableDropRequestData request) =>
            EncodeDrop(in request).ToByteArray();

        public static bool TryDecodeDrop(PE.PickableDropRequest message,
            out PickableDropRequestData request, out string reason)
        {
            request = default;
            reason = null;
            if (message == null)
            {
                reason = "drop-request-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "drop-sequence-" + message.Sequence;
                return false;
            }
            if (message.ActiveSlot < 0 || message.ActiveSlot > MaxActiveSlot)
            {
                reason = "drop-slot-" + message.ActiveSlot;
                return false;
            }
            if (message.ItemValue < 0)
            {
                reason = "drop-item-" + message.ItemValue;
                return false;
            }
            if (message.Count < 0)
            {
                reason = "drop-count-" + message.Count;
                return false;
            }
            int source = (int)message.Source;
            if (!DropSources.IsKnown(source))
            {
                reason = "drop-source-" + source;
                return false;
            }
            if (!EntityCodec.TryVector(message.Position, "drop-position", out Vector3 position,
                    out reason))
                return false;
            if (!EntityCodec.TryDirection(message.Direction, "drop-direction",
                    out Vector3 direction, out reason))
                return false;
            request = new PickableDropRequestData(message.Sequence, message.ActiveSlot,
                message.ItemValue, message.Count, position, direction, source, message.HasReach,
                message.Reach);
            return true;
        }

        public static PE.PickableDropResult EncodeDropResult(in PickableDropResultData result)
        {
            var message = new PE.PickableDropResult
            {
                Sequence = result.Sequence,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                ItemValue = result.ItemValue,
                Count = result.Count,
                InventoryVersion = result.InventoryVersion,
                PickableId = result.PickableId
            };
            if (result.Accepted)
            {
                message.Position = EntityCodec.ToVec3(result.Position);
                message.Velocity = EntityCodec.ToVec3(result.Velocity);
            }
            for (int i = 0; i < result.Indices.Length; i++)
            {
                message.Inventory.Add(new PE.InventorySlotDelta
                {
                    Index = result.Indices[i],
                    Value = result.Values[i],
                    Count = result.Counts[i]
                });
            }
            return message;
        }

        public static bool TryDecodeDropResult(PE.PickableDropResult message,
            out PickableDropResultData result, out string reason)
        {
            result = null;
            reason = null;
            if (message == null)
            {
                reason = "drop-result-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "drop-result-sequence-" + message.Sequence;
                return false;
            }
            if (!TryValidateVerdict(message.Accepted, message.Reason, "drop", out reason))
                return false;
            if (message.ItemValue < 0)
            {
                reason = "drop-result-item-" + message.ItemValue;
                return false;
            }
            if (message.Count < 0)
            {
                reason = "drop-result-count-" + message.Count;
                return false;
            }
            if (message.Accepted)
            {
                if (message.ItemValue <= 0)
                {
                    reason = "drop-result-item-" + message.ItemValue;
                    return false;
                }
                if (message.Count <= 0)
                {
                    reason = "drop-result-count-" + message.Count;
                    return false;
                }
                if (!EntityCodec.TryVector(message.Position, "drop-result-position",
                        out Vector3 position, out reason) ||
                    !EntityCodec.TryVector(message.Velocity, "drop-result-velocity",
                        out Vector3 velocity, out reason))
                    return false;
                if (!EntityCodec.TryDecodeInventorySlots(message.Inventory, out int[] indices,
                        out int[] values, out int[] counts, out reason))
                    return false;
                result = new PickableDropResultData(message.Sequence, true, string.Empty,
                    message.ItemValue, message.Count, position, velocity, indices, values,
                    counts, message.InventoryVersion, message.PickableId);
                return true;
            }
            if (!EntityCodec.TryDecodeInventorySlots(message.Inventory, out int[] rIndices,
                    out int[] rValues, out int[] rCounts, out reason))
                return false;
            result = new PickableDropResultData(message.Sequence, false, message.Reason,
                message.ItemValue, message.Count, Vector3.Zero, Vector3.Zero, rIndices, rValues,
                rCounts, 0);
            return true;
        }

        // ------------------------------------------------------------ 开火

        public static PE.ProjectileFireRequest EncodeFire(in ProjectileFireRequestData request)
        {
            var message = new PE.ProjectileFireRequest
            {
                Sequence = request.Sequence,
                Kind = request.Kind,
                ItemValue = request.ItemValue,
                ActiveSlot = request.ActiveSlot,
                Origin = EntityCodec.ToVec3(request.Origin),
                Direction = EntityCodec.ToVec3(request.Direction),
                Charge = request.Charge
            };
            if (request.HasReach) message.Reach = request.Reach;
            return message;
        }

        public static byte[] EncodeFireBytes(in ProjectileFireRequestData request) =>
            EncodeFire(in request).ToByteArray();

        public static bool TryDecodeFire(PE.ProjectileFireRequest message,
            out ProjectileFireRequestData request, out string reason)
        {
            request = default;
            reason = null;
            if (message == null)
            {
                reason = "fire-request-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "fire-sequence-" + message.Sequence;
                return false;
            }
            int kind = (int)message.Kind;
            if (!ProjectileFireKinds.IsKnown(kind))
            {
                reason = "fire-kind-" + kind;
                return false;
            }
            if (message.ItemValue <= 0)
            {
                reason = "fire-item-" + message.ItemValue;
                return false;
            }
            if (message.ActiveSlot < 0 || message.ActiveSlot > MaxActiveSlot)
            {
                reason = "fire-slot-" + message.ActiveSlot;
                return false;
            }
            if (message.Charge < 0 || message.Charge > MaxCharge)
            {
                reason = "fire-charge-" + message.Charge;
                return false;
            }
            if (!EntityCodec.TryVector(message.Origin, "fire-origin", out Vector3 origin,
                    out reason))
                return false;
            if (!EntityCodec.TryDirection(message.Direction, "fire-direction",
                    out Vector3 direction, out reason))
                return false;
            request = new ProjectileFireRequestData(message.Sequence, kind, message.ItemValue,
                message.ActiveSlot, origin, direction, message.Charge, message.HasReach,
                message.Reach);
            return true;
        }

        public static PE.ProjectileFireResult EncodeFireResult(
            in ProjectileFireResultData result)
        {
            var message = new PE.ProjectileFireResult
            {
                Sequence = result.Sequence,
                Accepted = result.Accepted,
                Reason = result.Reason ?? string.Empty,
                Kind = result.Kind,
                ItemValue = result.ItemValue,
                Spawned = result.Spawned,
                InventoryVersion = result.InventoryVersion
            };
            if (result.Accepted)
            {
                message.Position = EntityCodec.ToVec3(result.Position);
                message.Velocity = EntityCodec.ToVec3(result.Velocity);
            }
            for (int i = 0; i < result.Indices.Length; i++)
            {
                message.Inventory.Add(new PE.InventorySlotDelta
                {
                    Index = result.Indices[i],
                    Value = result.Values[i],
                    Count = result.Counts[i]
                });
            }
            return message;
        }

        public static bool TryDecodeFireResult(PE.ProjectileFireResult message,
            out ProjectileFireResultData result, out string reason)
        {
            result = null;
            reason = null;
            if (message == null)
            {
                reason = "fire-result-null";
                return false;
            }
            if (message.Sequence <= 0)
            {
                reason = "fire-result-sequence-" + message.Sequence;
                return false;
            }
            int kind = (int)message.Kind;
            if (message.Accepted && !ProjectileFireKinds.IsKnown(kind))
            {
                reason = "fire-result-kind-" + kind;
                return false;
            }
            if (!TryValidateVerdict(message.Accepted, message.Reason, "fire", out reason))
                return false;
            if (message.ItemValue < 0)
            {
                reason = "fire-result-item-" + message.ItemValue;
                return false;
            }
            if (message.Spawned < 0)
            {
                reason = "fire-result-spawned-" + message.Spawned;
                return false;
            }
            if (message.Accepted)
            {
                if (message.Spawned > 0)
                {
                    if (!EntityCodec.TryVector(message.Position, "fire-result-position",
                            out Vector3 position, out reason) ||
                        !EntityCodec.TryVector(message.Velocity, "fire-result-velocity",
                            out Vector3 velocity, out reason))
                        return false;
                    if (!EntityCodec.TryDecodeInventorySlots(message.Inventory, out int[] indices,
                            out int[] values, out int[] counts, out reason))
                        return false;
                    result = new ProjectileFireResultData(message.Sequence, true, string.Empty,
                        kind, message.ItemValue, message.Spawned, position, velocity, indices,
                        values, counts, message.InventoryVersion);
                    return true;
                }
                if (!EntityCodec.TryDecodeInventorySlots(message.Inventory, out int[] nIndices,
                        out int[] nValues, out int[] nCounts, out reason))
                    return false;
                result = new ProjectileFireResultData(message.Sequence, true, string.Empty, kind,
                    message.ItemValue, 0, Vector3.Zero, Vector3.Zero, nIndices, nValues,
                    nCounts, message.InventoryVersion);
                return true;
            }
            if (!EntityCodec.TryDecodeInventorySlots(message.Inventory, out int[] rIndices,
                    out int[] rValues, out int[] rCounts, out reason))
                return false;
            if (message.Spawned != 0)
            {
                // 被拒绝就不可能有权威投射物；伪造 spawned>0 的拒绝回执按非法载荷处理。
                reason = "fire-rejected-with-spawned-" + message.Spawned;
                return false;
            }
            result = new ProjectileFireResultData(message.Sequence, false, message.Reason, 0, 0, 0,
                Vector3.Zero, Vector3.Zero, rIndices, rValues, rCounts, 0);
            return true;
        }

        // ------------------------------------------------------------ 工具

        private static bool TryValidateVerdict(bool accepted, string reason, string kind,
            out string error)
        {
            error = null;
            if (!accepted && string.IsNullOrEmpty(reason))
            {
                error = kind + "-rejection-without-reason";
                return false;
            }
            if (accepted && !string.IsNullOrEmpty(reason))
            {
                error = kind + "-accepted-with-reason";
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 客户端 → 主机请求的**理由计数**（拒绝原因是固定词汇，实例对象、无静态状态）。
    /// 状态行 / 自检都读它：每个拒绝原因都有明确次数，绝不静默。
    /// </summary>
    public sealed class RequestReasonTally
    {
        private readonly Dictionary<string, long> m_counts =
            new Dictionary<string, long>(StringComparer.Ordinal);

        public long Total { get; private set; }

        public void Add(string reason)
        {
            string key = string.IsNullOrEmpty(reason) ? "unspecified" : reason;
            m_counts.TryGetValue(key, out long count);
            m_counts[key] = count + 1;
            Total++;
        }

        public long Get(string reason)
        {
            if (string.IsNullOrEmpty(reason)) reason = "unspecified";
            return m_counts.TryGetValue(reason, out long count) ? count : 0;
        }

        public void Clear()
        {
            m_counts.Clear();
            Total = 0;
        }

        public string Describe()
        {
            if (m_counts.Count == 0) return "none";
            var keys = new List<string>(m_counts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(keys[i]).Append('=').Append(m_counts[keys[i]]);
            }
            return builder.ToString();
        }
    }
}

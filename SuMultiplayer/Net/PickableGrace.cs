using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Engine;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 主机侧"掉落者拾取宽限"表（**纯托管**，P:\tmp\tsverify 直接编译本文件自检）。
    ///
    /// 为什么需要：主机用引擎自己的 <c>SubsystemPickables.AddPickable</c> 生成手丢 / 方块破坏掉落，
    /// 落点就在玩家脚下（手丢是 body + 0.25*Forward，方块破坏是块中心）。引擎只给 0.5s 的创建宽限
    /// （<c>SubsystemPickables.Update</c> 的 <c>num5 &gt; 0.5</c> 才允许拾取），客户端 20Hz 扫描在
    /// 1.0m 内就会发 <c>PickableAcquireRequest</c> —— 于是刚丢出去的物品立刻被自己捡回来，
    /// 既看不到"飞出去"，也等于丢弃操作无效。
    ///
    /// 这里给**掉落者本人**一段免疫窗口（默认 1.5s，真实时间）：窗口内主机对
    /// <c>PickableAcquireRequest</c> 明确回 <c>pickup-grace</c>；其他玩家不受影响（照常可捡）。
    /// 状态全在实例上，没有静态字段。键 = 主机分配的网络 id（<c>PickableReplicaEngine</c> 的对象映射）。
    /// </summary>
    public sealed class PickupGraceTable
    {
        /// <summary>默认宽限（秒，真实时间；与引擎 CreationTime 的游戏时间口径分开，不受时间加速影响）。</summary>
        public const double DefaultGraceSeconds = 1.5;

        private struct Entry
        {
            public int ClientId;
            public double Until;
        }

        private readonly Dictionary<int, Entry> m_entries = new Dictionary<int, Entry>();

        /// <summary>本表的口径（实例属性，便于按配置调整；默认 1.5s）。</summary>
        public double GraceSeconds { get; set; } = DefaultGraceSeconds;

        /// <summary>累计登记次数（每次主机为某玩家生成掉落记一次）。</summary>
        public long Registered { get; private set; }
        /// <summary>累计"宽限内拒绝掉落者拾取"的次数（每次被拒绝的请求 +1）。</summary>
        public long Blocked { get; private set; }
        /// <summary>当前还在宽限内的条目数（状态行 / 自检）。</summary>
        public int Count => m_entries.Count;

        /// <summary>主机生成一个拾取物后登记：<paramref name="clientId"/> 在 <see cref="GraceSeconds"/> 内不能拾取它。</summary>
        public void Register(int pickableId, int clientId, double now)
        {
            if (pickableId <= 0 || clientId <= 0) return;
            m_entries[pickableId] = new Entry { ClientId = clientId, Until = now + GraceSeconds };
            Registered++;
        }

        /// <summary>
        /// 这个玩家现在能不能拾取这个拾取物？false = 在宽限内（调用方必须回 <c>pickup-grace</c>）。
        /// 只有**掉落者本人**会被挡；其他客户端不受影响。
        /// </summary>
        public bool CanAcquire(int pickableId, int clientId, double now)
        {
            if (pickableId <= 0 || clientId <= 0) return true;
            if (!m_entries.TryGetValue(pickableId, out Entry entry)) return true;
            if (now >= entry.Until)
            {
                m_entries.Remove(pickableId);
                return true;
            }
            if (entry.ClientId != clientId) return true;
            Blocked++;
            return false;
        }

        /// <summary>清掉已经过期的条目（主机抓取拾取物的 32Hz 脉冲里调用；有界）。</summary>
        public int Prune(double now)
        {
            if (m_entries.Count == 0) return 0;
            List<int> expired = null;
            foreach (KeyValuePair<int, Entry> item in m_entries)
            {
                if (now < item.Value.Until) continue;
                if (expired == null) expired = new List<int>(4);
                expired.Add(item.Key);
            }
            if (expired == null) return 0;
            for (int i = 0; i < expired.Count; i++) m_entries.Remove(expired[i]);
            return expired.Count;
        }

        /// <summary>拾取成功 / 对象消失后不必再留着（明确移除，避免 id 复用误挡）。</summary>
        public bool Remove(int pickableId) => m_entries.Remove(pickableId);

        public void Clear() => m_entries.Clear();

        public string Describe() =>
            "grace=" + GraceSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s" +
            " registered=" + Registered.ToString(CultureInfo.InvariantCulture) +
            " blocked=" + Blocked.ToString(CultureInfo.InvariantCulture) +
            " active=" + m_entries.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 主机生成掉落时的速度口径（**纯托管**，自检直接编译本文件）。
    ///
    /// Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
    ///         （playerInput.Drop → velocity = 8f * CreateFromQuaternion(EyeRotation).Forward）
    /// Source: Survivalcraft/Game/SubsystemPickables.cs:SubsystemPickables.AddPickable
    ///         （velocity = null → 随机 x/z ∈ [-0.5,0.5]、y ∈ [1,1.2] 的轻微上抛）
    /// Source: Survivalcraft/Game/SubsystemTerrain.cs:SubsystemTerrain.DestroyCell
    ///         （方块破坏 → AddPickable(..., position = cell + 0.5, velocity: null, stuckMatrix: null)）
    ///
    /// 手丢用引擎原样的 8 m/s × 视线方向；方块破坏用引擎默认随机上抛（不自己编速度）。
    /// UI 拖出到世界同样是引擎自己的口径：拖拽射线方向 × 12 m/s（见 <see cref="ForUiDrop"/>）。
    /// </summary>
    public static class HostDropVelocity
    {
        /// <summary>引擎 Drop 分支的手丢速度（m/s）。</summary>
        public const float ManualSpeed = 8f;

        /// <summary>
        /// 引擎 UI 拖出到世界的速度（m/s）。
        /// Source: Survivalcraft/Game/ViewWidget.cs:ViewWidget.DragDrop
        ///         （Normalize(ScreenToWorld(drag) - ActiveCamera.ViewPosition) * 12f）
        /// </summary>
        public const float UiDropSpeed = 12f;

        /// <summary>手丢速度：与引擎 Drop 分支完全同形（视线方向 × 8 m/s）。</summary>
        public static Vector3 ForManualDrop(Vector3 eyeForward) => ManualSpeed * eyeForward;

        /// <summary>UI 拖出速度：与引擎 ViewWidget.DragDrop 完全同形（拖拽方向 × 12 m/s）。</summary>
        public static Vector3 ForUiDrop(Vector3 dragDirection) => UiDropSpeed * dragDirection;

        /// <summary>方向是否可用于生成速度（有限且非零）。</summary>
        public static bool IsUsableDirection(Vector3 direction)
        {
            if (float.IsNaN(direction.X) || float.IsInfinity(direction.X) ||
                float.IsNaN(direction.Y) || float.IsInfinity(direction.Y) ||
                float.IsNaN(direction.Z) || float.IsInfinity(direction.Z))
                return false;
            return direction.LengthSquared() > 1e-6f;
        }
    }

    /// <summary>
    /// 客户端侧"这个刚到的副本是不是本机刚造成的掉落"预测表（**纯托管**，自检直接编译本文件）。
    ///
    /// 客户端本地引擎在玩家挖 / 放时也会跑 <c>ComponentMiner.Dig/Place → SubsystemTerrain.DestroyCell</c>，
    /// 于是本地先生成一份**预测掉落物**；<c>PickableReplicaEngine.CullForeignClientPickables</c> 会把它清掉
    /// （拾取物只能由主机创建）。清掉之前把"物品 + 落点"记在这里；等主机的权威副本（EntityState ADD）
    /// 到达时按同样的物品 / 落点认领，模块就给该副本登记拾取宽限 —— 这样**本机编辑造成的掉落**在
    /// 宽限内也不会被本机的拾取扫描发请求（主机也会用 <c>pickup-grace</c> 拒绝，两头一致）。
    ///
    /// 纯策略、有界（最多 64 条，过期即丢），无静态状态。
    /// </summary>
    public sealed class OwnDropPredictionTable
    {
        /// <summary>预测有效期（秒）：主机副本在一个 RTT 后到达；5s 足够覆盖卡顿，又不会长期误认。</summary>
        public const double DefaultLifetimeSeconds = 5.0;
        /// <summary>有界上限（一次挖掘最多几个掉落；64 足够覆盖连续挖掘）。</summary>
        public const int MaxEntries = 64;
        /// <summary>认领距离（平方）：本地预测与主机副本都在块中心 (x,y,z)+0.5，3m 足够宽。</summary>
        public const float ClaimDistanceSquared = 9f;

        private struct Entry
        {
            public int Contents;
            public Vector3 Position;
            public double Expiry;
            public bool Consumed;
        }

        private readonly List<Entry> m_entries = new List<Entry>();

        /// <summary>累计登记（本地预测掉落）数量。</summary>
        public long Remembered { get; private set; }
        /// <summary>累计被主机副本认领的数量（= 本机造成的掉落拿到拾取宽限的次数）。</summary>
        public long Claimed { get; private set; }
        /// <summary>当前还没被认领、也没过期的条目数。</summary>
        public int Pending
        {
            get
            {
                int count = 0;
                for (int i = 0; i < m_entries.Count; i++)
                    if (!m_entries[i].Consumed) count++;
                return count;
            }
        }

        /// <summary>记一条本地预测掉落（contents = 物品 block 内容 id；position = 本地生成点）。</summary>
        public void Remember(int contents, Vector3 position, double now,
            double lifetimeSeconds = DefaultLifetimeSeconds)
        {
            if (contents == 0) return;
            Prune(now);
            if (m_entries.Count >= MaxEntries)
            {
                // 有界：先丢已认领的，再丢最旧的（不静默无限增长）。
                for (int i = 0; i < m_entries.Count; i++)
                {
                    if (!m_entries[i].Consumed) continue;
                    m_entries.RemoveAt(i);
                    break;
                }
                if (m_entries.Count >= MaxEntries) m_entries.RemoveAt(0);
            }
            m_entries.Add(new Entry
            {
                Contents = contents,
                Position = position,
                Expiry = now + Math.Max(0.1, lifetimeSeconds)
            });
            Remembered++;
        }

        /// <summary>
        /// 主机副本到达：如果它和某条未认领的本地预测是同一物品、同一落点 → 认领（消费该条）并返回 true。
        /// 一条预测只匹配一个主机副本（同一方块掉 3 个雪球 = 3 条预测 / 3 次认领）。
        /// </summary>
        public bool TryClaim(int contents, Vector3 position, double now,
            float maxDistanceSquared = ClaimDistanceSquared)
        {
            Prune(now);
            for (int i = 0; i < m_entries.Count; i++)
            {
                Entry entry = m_entries[i];
                if (entry.Contents != contents) continue;
                if (Vector3.DistanceSquared(entry.Position, position) > maxDistanceSquared)
                    continue;
                entry.Consumed = true;
                m_entries[i] = entry;
                Claimed++;
                return true;
            }
            return false;
        }

        /// <summary>清掉过期 / 已认领条目；返回移除条数。</summary>
        public int Prune(double now)
        {
            if (m_entries.Count == 0) return 0;
            int removed = 0;
            for (int i = m_entries.Count - 1; i >= 0; i--)
            {
                Entry entry = m_entries[i];
                if (!entry.Consumed && now < entry.Expiry) continue;
                m_entries.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        public void Clear() => m_entries.Clear();

        public string Describe() =>
            "remembered=" + Remembered.ToString(CultureInfo.InvariantCulture) +
            " claimed=" + Claimed.ToString(CultureInfo.InvariantCulture) +
            " pending=" + Pending.ToString(CultureInfo.InvariantCulture);
    }
}

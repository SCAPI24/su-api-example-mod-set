using System;
using System.Collections.Generic;
using System.Text;

namespace SuMultiplayer.Requests
{
    /// <summary>主机侧"客户端事件请求"的种类（append-only：数值一旦下发不再改）。</summary>
    public static class HostRequestKind
    {
        /// <summary>加入请求（JoinApproval 的挂起条目）。</summary>
        public const int Join = 1;

        public static string Name(int kind) => kind == Join ? "join" : "kind-" + kind;
    }

    /// <summary>队列里的一条请求（不可变快照；决议通过 <see cref="HostRequestQueue.Resolve"/> 走回调）。</summary>
    public sealed class HostRequestEntry
    {
        /// <summary>队列内自增 id（展示 / 决议寻址用；同 clientId 重发会作废旧条目、新条目拿新 id）。</summary>
        public long Id { get; }
        /// <summary><see cref="HostRequestKind"/>。</summary>
        public int Kind { get; }
        /// <summary>发起端的主机侧 clientId。</summary>
        public int ClientId { get; }
        /// <summary>发起端显示名（加入请求 = 玩家名）。</summary>
        public string Name { get; }
        /// <summary>发起端身份短标（日志 / 展示）。</summary>
        public string Identity { get; }
        /// <summary>请求到达时刻（Time.RealTime 秒；展示按它倒序 = 最新在上）。</summary>
        public double CreatedSeconds { get; }
        /// <summary>决议回调：approved = Allow；!approved = Reject（Later 不走这里，见 Hold）。</summary>
        internal Action<bool, string> Decided { get; }

        internal HostRequestEntry(long id, int kind, int clientId, string name, string identity,
            double createdSeconds, Action<bool, string> decided)
        {
            Id = id;
            Kind = kind;
            ClientId = clientId;
            Name = name ?? string.Empty;
            Identity = identity ?? string.Empty;
            CreatedSeconds = createdSeconds;
            Decided = decided;
        }

        public string Describe() => "#" + Id + " " + HostRequestKind.Name(Kind) +
            " clientId=" + ClientId + " " + (string.IsNullOrEmpty(Name) ? "(unnamed)" : Name);
    }

    /// <summary>
    /// 主机侧**通用**"客户端事件请求"队列（2026-10-26）：加入审批是第一个用户，后续
    /// 世界控制 / 传送 / 资料等请求都走这里 —— IF 按钮的队列页只认这个组件，不认 JoinApproval。
    ///
    /// 口径（用户定）：
    ///   · 容量 8；**满了之后新请求不处理（忽略）** —— 主机按 busy 拒绝，客户端可重新发起；
    ///   · **同一 clientId 的同种类重复请求 = 旧条目过期作废、新条目按新时间入队**；
    ///   · Later（稍后）= 条目留在队列里（<see cref="Hold"/> 决议），由 IF 按钮随时再弹出处理；
    ///   · Allow / Reject = 条目出队并回调决议（每条只决议一次）。
    /// 纯托管、无静态状态、无引擎引用（P:\tmp\tsverify 可直接编译自检）。
    /// </summary>
    public sealed class HostRequestQueue
    {
        /// <summary>队列容量（用户定 8；超出 = 忽略新请求，不挤掉旧的）。</summary>
        public const int Capacity = 8;

        private readonly object m_gate = new object();
        private readonly List<HostRequestEntry> m_entries = new List<HostRequestEntry>();
        private long m_nextId = 1;

        // ---- 计数（状态行 / 自检可审计）
        public long Enqueued { get; private set; }
        public long DuplicatedReplaced { get; private set; }
        public long OverflowIgnored { get; private set; }
        public long Approved { get; private set; }
        public long Rejected { get; private set; }
        public long Held { get; private set; }

        /// <summary>当前待决条数（IF 按钮文案用）。</summary>
        public int Count
        {
            get { lock (m_gate) return m_entries.Count; }
        }

        /// <summary>是否已有该端该种类的待决请求（入队前去重展示用）。</summary>
        public bool HasPending(int kind, int clientId)
        {
            lock (m_gate)
            {
                for (int i = 0; i < m_entries.Count; i++)
                {
                    if (m_entries[i].Kind == kind && m_entries[i].ClientId == clientId) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 入队一条请求。true = 已入队（返回条目）；false = 队列满被忽略（调用方按 busy 拒绝）。
        /// 同 clientId 同 kind 的旧条目会**过期作废**（其决议回调被置为失效），新条目按新时间入队。
        /// </summary>
        public bool TryEnqueue(int kind, int clientId, string name, string identity,
            double nowSeconds, Action<bool, string> decided, out HostRequestEntry entry)
        {
            entry = null;
            lock (m_gate)
            {
                // 同端同种类的旧条目过期作废。
                for (int i = m_entries.Count - 1; i >= 0; i--)
                {
                    if (m_entries[i].Kind != kind || m_entries[i].ClientId != clientId) continue;
                    m_entries.RemoveAt(i);
                    DuplicatedReplaced++;
                }
                if (m_entries.Count >= Capacity)
                {
                    OverflowIgnored++;
                    return false;
                }
                entry = new HostRequestEntry(m_nextId++, kind, clientId, name, identity,
                    nowSeconds, decided);
                m_entries.Add(entry);
                Enqueued++;
                return true;
            }
        }

        /// <summary>
        /// 按时间倒序快照（最新在上；IF 队列页直接展示这份）。
        /// </summary>
        public List<HostRequestEntry> Snapshot()
        {
            lock (m_gate)
            {
                var copy = new List<HostRequestEntry>(m_entries);
                copy.Sort((a, b) => b.CreatedSeconds.CompareTo(a.CreatedSeconds));
                return copy;
            }
        }

        /// <summary>弹出最新一条（不移除；Allow / Reject / Hold 决议时才出队）。空队列返回 null。</summary>
        public HostRequestEntry PeekLatest()
        {
            lock (m_gate)
            {
                if (m_entries.Count == 0) return null;
                HostRequestEntry latest = m_entries[0];
                for (int i = 1; i < m_entries.Count; i++)
                {
                    if (m_entries[i].CreatedSeconds > latest.CreatedSeconds) latest = m_entries[i];
                }
                return latest;
            }
        }

        /// <summary>Allow：出队并回调 approved=true（每条只决议一次）。</summary>
        public bool Approve(long id, string note)
        {
            return Resolve(id, approved: true, note);
        }

        /// <summary>Reject：出队并回调 approved=false。</summary>
        public bool Reject(long id, string reason)
        {
            return Resolve(id, approved: false, reason);
        }

        /// <summary>
        /// Later：条目**留在队列**，回调本条目暂不触发（决议权交还 IF 按钮）。
        /// true = 找到了这条待决请求（它仍可被 Approve / Reject / 再次 Hold）。
        /// </summary>
        public bool Hold(long id)
        {
            lock (m_gate)
            {
                for (int i = 0; i < m_entries.Count; i++)
                {
                    if (m_entries[i].Id != id) continue;
                    Held++;
                    return true;
                }
                return false;
            }
        }

        /// <summary>客户端断开 / 世界重载：清掉某端的全部待决请求（决议回调静默作废）。</summary>
        public int RemoveAllFor(int clientId)
        {
            lock (m_gate)
            {
                int removed = 0;
                for (int i = m_entries.Count - 1; i >= 0; i--)
                {
                    if (m_entries[i].ClientId != clientId) continue;
                    m_entries.RemoveAt(i);
                    removed++;
                }
                return removed;
            }
        }

        /// <summary>清空（世界重载 / 会话结束）。</summary>
        public void Clear()
        {
            lock (m_gate) m_entries.Clear();
        }

        private bool Resolve(long id, bool approved, string note)
        {
            Action<bool, string> decided = null;
            lock (m_gate)
            {
                for (int i = 0; i < m_entries.Count; i++)
                {
                    if (m_entries[i].Id != id) continue;
                    decided = m_entries[i].Decided;
                    m_entries.RemoveAt(i);
                    break;
                }
            }
            if (decided == null) return false;
            if (approved) Approved++;
            else Rejected++;
            try
            {
                decided(approved, note ?? string.Empty);
            }
            catch
            {
                // 决议回调异常不回滚决议（Terrain 层自身有锁与幂等）；调用方不依赖结果。
            }
            return true;
        }

        public string Describe() => "count=" + Count + " enqueued=" + Enqueued +
            " replaced=" + DuplicatedReplaced + " overflow=" + OverflowIgnored +
            " approved=" + Approved + " rejected=" + Rejected + " held=" + Held;
    }
}

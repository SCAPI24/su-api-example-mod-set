using System;
using System.Collections.Generic;
using System.Globalization;

namespace HeadlessRenderingMod
{
    /// <summary>
    /// 主机侧数据修改（DM）决策的小环形缓冲：游戏线程写入，控制台线程读取。
    ///
    /// 记的是**主机自己做过的事**：收到哪条 DM 请求、有没有按 server.json 名单自动同意、
    /// 控制台里手动允许/拒绝、以及联机 mod 回传的 Result 回执。
    /// 远端服务器没有 GPU、控制台是唯一界面，菜单里能直接看到"刚才那条请求走到哪一步"，
    /// 不用去翻 Game.log。
    /// </summary>
    internal sealed class DataModificationFeed
    {
        public const int Capacity = 16;

        internal sealed class Entry
        {
            /// <summary>写入时刻（本地时间 HH:mm:ss）。</summary>
            public string Time = string.Empty;

            /// <summary>request / auto-approve / manual / result。</summary>
            public string Kind = "request";

            /// <summary>结果码名（Request / AutoApproved / Applied / Failed / Rejected…）。</summary>
            public string Code = string.Empty;

            public string ModId = string.Empty;

            public string Operation = string.Empty;

            public int SourceClientId = -1;

            public int RequestId = -1;

            /// <summary>发起方的记录键（账号 userid，或 name:名字）；结果回执里没有这个字段。</summary>
            public string SourceKey = string.Empty;

            public string Details = string.Empty;

            /// <summary>一行摘要（控制台菜单里直接显示）。</summary>
            public string Describe()
            {
                string text = Time + "  " + (string.IsNullOrEmpty(Code) ? Kind : Code) + "  " +
                    (string.IsNullOrEmpty(ModId) ? "Mod" : ModId) + "/" +
                    (string.IsNullOrEmpty(Operation) ? "operation" : Operation);
                if (SourceClientId >= 0)
                    text += "  client " + SourceClientId.ToString(CultureInfo.InvariantCulture);
                if (RequestId >= 0)
                    text += "  request " + RequestId.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(Details))
                    text += "  -  " + Details;
                return text;
            }
        }

        private readonly object m_lock = new object();
        private readonly List<Entry> m_entries = new List<Entry>();

        public void Add(Entry entry)
        {
            if (entry == null)
                return;
            if (string.IsNullOrEmpty(entry.Time))
                entry.Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            lock (m_lock)
            {
                m_entries.Insert(0, entry);
                while (m_entries.Count > Capacity)
                    m_entries.RemoveAt(m_entries.Count - 1);
            }
        }

        /// <summary>最新在前的一份快照（拷贝，跨线程安全）。</summary>
        public List<Entry> Snapshot()
        {
            lock (m_lock)
                return new List<Entry>(m_entries);
        }

        public Entry Latest()
        {
            lock (m_lock)
                return m_entries.Count > 0 ? m_entries[0] : null;
        }
    }
}

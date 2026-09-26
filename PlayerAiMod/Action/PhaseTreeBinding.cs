using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// **相位 → 树包**的绑定（plan §4.13 的"两套树包"）：世界外用一棵、世界内用另一棵，
    /// 相位一变就**通过池调度**把活动树换过去。
    ///
    /// 为什么这么设计（三条都来自已有决策）：
    ///   · **换树仍然只有池一个来源**（D9：受控换树先不开）—— 绑定只是"替人写一次 `pool.next`"，
    ///     真正的切换还是 `PoolScheduler` 在**步骤边界**做（S1：正在跑的那一步绝不截断）；
    ///   · **触发点是相位边沿，不是每帧**：与交接协议同一个纪律（边沿触发），
    ///     否则刚切过去就会被下一次调度覆盖，两棵树互相抢；
    ///   · **不认识就说不认识**：相位名写错（`frontt`）必须**解析时报错**，
    ///     而不是"跑了半天发现从来没切过"——那是最难查的一类配置错。
    ///
    /// 例子：`front=demo.front,world=demo.laya`
    /// （`front` 界面上点进世界 → 相位变 `world` → 池切到 `demo.laya`；退出世界 → 切回 `demo.front`。）
    /// </summary>
    public sealed class PhaseTreeBinding
    {
        private readonly Dictionary<string, string> m_entries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Entries
        {
            get { return m_entries; }
        }

        public int Count
        {
            get { return m_entries.Count; }
        }

        /// <summary>解析 `相位=树包,相位=树包`。**任何一项写错都整条拒绝**（不做部分生效）。</summary>
        public static bool TryParse(string spec, out PhaseTreeBinding binding, out string error)
        {
            binding = null;
            error = null;

            var parsed = new PhaseTreeBinding();
            if (string.IsNullOrEmpty(spec) || spec.Trim().Length == 0)
            {
                binding = parsed;   // 空 = 清空绑定（合法）
                return true;
            }

            string[] parts = spec.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length == 0)
                    continue;

                int equals = part.IndexOf('=');
                if (equals <= 0 || equals >= part.Length - 1)
                {
                    error = "binding entry '" + part + "' must be written as <phase>=<tree package>";
                    return false;
                }

                string phase = part.Substring(0, equals).Trim().ToLowerInvariant();
                string key = part.Substring(equals + 1).Trim();
                if (!PhaseNames.IsKnown(phase))
                {
                    error = "unknown phase '" + phase + "' in '" + part
                        + "' (known: front / loading / world)";
                    return false;
                }
                if (key.Length == 0)
                {
                    error = "binding entry '" + part + "' has no tree package name";
                    return false;
                }
                parsed.m_entries[phase] = key;
            }

            binding = parsed;
            return true;
        }

        /// <summary>这个相位绑了哪棵树（没绑返回 false）。</summary>
        public bool TryResolve(string phase, out string key)
        {
            key = null;
            if (string.IsNullOrEmpty(phase))
                return false;
            return m_entries.TryGetValue(phase.Trim(), out key) && !string.IsNullOrEmpty(key);
        }

        public string Describe()
        {
            if (m_entries.Count == 0)
                return "(no phase binding)";
            var parts = new List<string>();
            foreach (KeyValuePair<string, string> pair in m_entries)
                parts.Add(pair.Key + "=" + pair.Value);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(",", parts.ToArray());
        }

        public override string ToString()
        {
            return Describe();
        }
    }
}

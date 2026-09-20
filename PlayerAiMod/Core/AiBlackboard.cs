using System;
using System.Collections.Generic;

namespace PlayerAiMod
{
    /// <summary>
    /// 类型化黑板键。同名键共享同一格数据；用泛型键避免不同状态之间"同名不同类型"的读写事故。
    /// </summary>
    public sealed class AiBlackboardKey<T>
    {
        public AiBlackboardKey(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Blackboard key name must not be empty.", nameof(name));
            Name = name;
        }

        public string Name { get; }

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// 状态之间共享数据的黑板。
    ///
    /// 约定：
    ///   · 单线程使用 —— 只在游戏线程的 AI tick 内读写，因此不加锁。
    ///   · 数据是"当前认知"，不是游戏状态；写入黑板不会改变游戏，只是把观察结果/决策意图留给后续状态使用。
    ///   · Revision 每写一次自增，状态可用它判断"这一帧有没有新信息"，避免无谓重算。
    /// </summary>
    public sealed class AiBlackboard
    {
        private readonly Dictionary<string, object> m_values =
            new Dictionary<string, object>(StringComparer.Ordinal);

        /// <summary>写入计数：任何一次 Set/Remove/Clear 都会自增。</summary>
        public int Revision { get; private set; }

        public int Count
        {
            get { return m_values.Count; }
        }

        public void Set<T>(AiBlackboardKey<T> key, T value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));
            m_values[key.Name] = value;
            Revision++;
        }

        public bool TryGet<T>(AiBlackboardKey<T> key, out T value)
        {
            if (key == null)
                throw new ArgumentNullException(nameof(key));

            object raw;
            if (m_values.TryGetValue(key.Name, out raw) && raw is T)
            {
                value = (T)raw;
                return true;
            }

            value = default(T);
            return false;
        }

        /// <summary>取不到（或类型不符）时返回 fallback，绝不抛异常 —— 状态分支里大量使用。</summary>
        public T Get<T>(AiBlackboardKey<T> key, T fallback = default(T))
        {
            T value;
            return TryGet(key, out value) ? value : fallback;
        }

        public bool Has<T>(AiBlackboardKey<T> key)
        {
            return key != null && m_values.ContainsKey(key.Name);
        }

        /// <summary>按名字判断键是否存在（包加载、装饰器、编辑器等按字符串操作的场合用）。</summary>
        public bool Has(string name)
        {
            return !string.IsNullOrEmpty(name) && m_values.ContainsKey(name);
        }

        /// <summary>按名字取值；类型不符视为不存在。</summary>
        public bool TryGet<T>(string name, out T value)
        {
            value = default(T);
            if (string.IsNullOrEmpty(name))
                return false;

            object raw;
            if (m_values.TryGetValue(name, out raw) && raw is T)
            {
                value = (T)raw;
                return true;
            }
            return false;
        }

        public bool Remove<T>(AiBlackboardKey<T> key)
        {
            if (key == null)
                return false;
            if (!m_values.Remove(key.Name))
                return false;
            Revision++;
            return true;
        }

        public void Clear()
        {
            if (m_values.Count == 0)
                return;
            m_values.Clear();
            Revision++;
        }

        public IEnumerable<string> Keys
        {
            get { return m_values.Keys; }
        }

        /// <summary>调试输出："key=value" 列表（值一律 ToString，仅供日志）。</summary>
        public override string ToString()
        {
            var builder = new System.Text.StringBuilder();
            builder.Append("AiBlackboard(").Append(m_values.Count).Append(")");
            foreach (KeyValuePair<string, object> pair in m_values)
            {
                builder.Append(' ').Append(pair.Key).Append('=')
                    .Append(pair.Value == null ? "null" : pair.Value.ToString());
            }
            return builder.ToString();
        }
    }
}

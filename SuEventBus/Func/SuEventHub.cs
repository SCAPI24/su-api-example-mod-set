using System;
using System.Collections.Generic;

namespace SuEventBus
{
    /// <summary>
    /// 极简同步事件总线（中间件）。
    ///
    /// 约定：
    ///   · 事件载荷一律是**可变类**对象；订阅者直接改字段表达"已接管 / 提供结果"；
    ///   · Publish 按订阅顺序**同步**回调，回调里再 Publish 其它事件是允许的；
    ///   · 不做任何异常吞噬：订阅者抛异常会向上冒泡（便于开发期定位）。
    ///
    /// 这样两个互不认识的 Mod（信道 Mod / ScMultiplayer）只要引用同一个 SuEventBus.dll，
    /// 就能监听同一事件而不必互相替换 Class 参数。
    /// </summary>
    public static class SuEventHub
    {
        private static readonly object s_lock = new object();

        private static readonly Dictionary<Type, List<Delegate>> s_handlers =
            new Dictionary<Type, List<Delegate>>();

        public static IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            Type key = typeof(TEvent);
            lock (s_lock)
            {
                if (!s_handlers.TryGetValue(key, out List<Delegate> list))
                {
                    list = new List<Delegate>();
                    s_handlers.Add(key, list);
                }
                list.Add(handler);
            }
            return new Subscription<TEvent>(handler);
        }

        public static void Unsubscribe<TEvent>(Action<TEvent> handler)
        {
            if (handler == null)
                return;

            lock (s_lock)
            {
                if (s_handlers.TryGetValue(typeof(TEvent), out List<Delegate> list))
                {
                    list.Remove(handler);
                    if (list.Count == 0)
                        s_handlers.Remove(typeof(TEvent));
                }
            }
        }

        public static void Publish<TEvent>(TEvent evt)
        {
            Delegate[] snapshot;
            lock (s_lock)
            {
                if (!s_handlers.TryGetValue(typeof(TEvent), out List<Delegate> list) || list.Count == 0)
                    return;
                snapshot = list.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                ((Action<TEvent>)snapshot[i])(evt);
            }
        }

        public static bool HasSubscribers<TEvent>()
        {
            lock (s_lock)
            {
                return s_handlers.TryGetValue(typeof(TEvent), out List<Delegate> list) && list.Count > 0;
            }
        }

        /// <summary>卸载 Mod 时清理全部订阅（中间件本身不持有状态）。</summary>
        public static void Clear()
        {
            lock (s_lock)
            {
                s_handlers.Clear();
            }
        }

        private sealed class Subscription<TEvent> : IDisposable
        {
            private Action<TEvent> m_handler;

            public Subscription(Action<TEvent> handler)
            {
                m_handler = handler;
            }

            public void Dispose()
            {
                Action<TEvent> handler = m_handler;
                m_handler = null;
                if (handler != null)
                    Unsubscribe(handler);
            }
        }
    }
}

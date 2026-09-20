using Engine;
using Engine.Input;
using System;
using System.Collections.Generic;

namespace CmdBridgeMod
{
    /// <summary>
    /// 热键注册表 —— 任何 Mod 都能注册"帧首判定"的热键（可复用基建）。
    ///
    /// 为什么不能直接在自己的 Update 里判 `IsKeyDownOnce`：
    ///   · 判定必须发生在**帧首**：`Keyboard.AfterFrame()` 会在帧末清空 downOnce 数组
    ///     （`Engine/Engine/Input/Mouse.cs:132-138` 同理），在帧内较晚的位置读会漏掉或重复。
    ///   · 注册/注销来自服务器线程（命令）而判定在游戏线程 → 需要线程安全。
    ///   · 触发次数、上次触发时间要能报给控制面（`hotkey.status`）。
    ///
    /// 语义：注册的委托在帧首于游戏线程执行，一次"按下沿"只触发一次。
    /// PlayerAiMod 用它绑定 `Home`（行为树执行/暂停）与 `PgUp/PgDn`（动作包录制）。
    /// </summary>
    internal sealed class HotkeyRegistry
    {
        private sealed class Entry
        {
            public string Name;
            public Key Key;
            public Action Handler;
            public int FireCount;
            public double LastFired;
        }

        private readonly object m_gate = new object();
        private readonly List<Entry> m_entries = new List<Entry>();
        private int m_totalFires;
        private string m_lastError;

        /// <summary>注册（同名覆盖）。返回 null 表示成功，否则返回错误说明。</summary>
        public string Register(string name, Key key, Action handler)
        {
            if (handler == null)
                return "handler is null";
            if (string.IsNullOrEmpty(name))
                name = key.ToString();

            lock (m_gate)
            {
                for (int i = 0; i < m_entries.Count; i++)
                {
                    if (string.Equals(m_entries[i].Name, name, StringComparison.Ordinal))
                    {
                        m_entries[i].Key = key;
                        m_entries[i].Handler = handler;
                        return null;
                    }
                }

                m_entries.Add(new Entry { Name = name, Key = key, Handler = handler });
                return null;
            }
        }

        public bool Unregister(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            lock (m_gate)
            {
                for (int i = 0; i < m_entries.Count; i++)
                {
                    if (string.Equals(m_entries[i].Name, name, StringComparison.Ordinal))
                    {
                        m_entries.RemoveAt(i);
                        return true;
                    }
                }
                return false;
            }
        }

        public void Clear()
        {
            lock (m_gate)
            {
                m_entries.Clear();
            }
        }

        public int Count
        {
            get { lock (m_gate) return m_entries.Count; }
        }

        /// <summary>帧首判定（游戏线程）。</summary>
        public void Tick()
        {
            Entry[] snapshot;
            lock (m_gate)
            {
                if (m_entries.Count == 0)
                    return;
                snapshot = m_entries.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                Entry entry = snapshot[i];
                if (!Keyboard.IsKeyDownOnce(entry.Key))
                    continue;

                entry.FireCount++;
                entry.LastFired = Time.RealTime;
                m_totalFires++;

                try
                {
                    entry.Handler();
                }
                catch (Exception exception)
                {
                    m_lastError = entry.Name + ": " + exception.GetType().Name + ": " + exception.Message;
                    Log.Warning("[CmdBridge] hotkey '" + entry.Name + "' handler failed: " + exception.Message);
                }
            }
        }

        public Dictionary<string, object> Describe()
        {
            var list = new List<Dictionary<string, object>>();
            lock (m_gate)
            {
                for (int i = 0; i < m_entries.Count; i++)
                {
                    Entry entry = m_entries[i];
                    list.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = entry.Name,
                        ["key"] = entry.Key.ToString(),
                        ["fired"] = entry.FireCount,
                        ["lastFired"] = entry.FireCount > 0 ? entry.LastFired : 0.0
                    });
                }
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["count"] = list.Count,
                ["totalFires"] = m_totalFires,
                ["hotkeys"] = list,
                ["lastError"] = m_lastError
            };
        }

        /// <summary>按名字解析按键（不区分大小写；接受 pgup/pgdn/esc/space 等常见别名）。</summary>
        public static bool TryParseKey(string name, out Key key)
        {
            key = default(Key);
            if (string.IsNullOrEmpty(name))
                return false;

            string normalized = name.Trim();
            switch (normalized.ToLowerInvariant())
            {
                case "pgup":
                case "pageup":
                    key = Key.PageUp;
                    return true;
                case "pgdn":
                case "pagedown":
                case "pgdown":
                    key = Key.PageDown;
                    return true;
                case "esc":
                case "escape":
                    key = Key.Escape;
                    return true;
                case "space":
                case "spacebar":
                    key = Key.Space;
                    return true;
                case "shift":
                    key = Key.Shift;
                    return true;
                case "enter":
                case "return":
                    key = Key.Enter;
                    return true;
            }

            return Enum.TryParse(normalized, true, out key);
        }
    }
}

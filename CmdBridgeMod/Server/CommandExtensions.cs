using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CmdBridgeMod
{
    /// <summary>
    /// 扩展命令抛出的错误：带上稳定的错误码，客户端与控制面按码判断。
    /// 其它异常一律归为 `command_failed`（ControlServer 的兜底）。
    /// </summary>
    public sealed class CmdBridgeCommandException : Exception
    {
        public CmdBridgeCommandException(string code, string message)
            : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? "command_failed" : code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// 扩展命令看到的请求：只暴露"读参数"，不暴露任何游戏对象，
    /// 于是别的 Mod 注册命令时不必依赖 CmdBridgeMod 的内部类型。
    /// </summary>
    public sealed class CmdBridgeCommandRequest
    {
        private readonly BridgeRequest m_request;

        internal CmdBridgeCommandRequest(BridgeRequest request)
        {
            m_request = request;
            Command = request != null ? request.Command : null;
            Arguments = BuildArguments(request);
        }

        /// <summary>命令名（小写，例如 "ai.status"）。</summary>
        public string Command { get; }

        /// <summary>
        /// 全部参数（JSON 基元 → CLR 基元；数组 → List&lt;object&gt;；对象 → 嵌套字典）。
        /// 需要"按自己的规则解释参数"的命令用这个；只读几个已知参数时用下面的类型化 getter 更省事。
        /// </summary>
        public IReadOnlyDictionary<string, object> Arguments { get; }

        private static IReadOnlyDictionary<string, object> BuildArguments(BridgeRequest request)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (request == null)
                return result;

            foreach (KeyValuePair<string, JsonElement> pair in request.EnumerateArguments())
                result[pair.Key] = ToClr(pair.Value);
            return result;
        }

        private static object ToClr(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out long integer) ? (object)integer : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Array:
                {
                    var items = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                        items.Add(ToClr(item));
                    return items;
                }
                case JsonValueKind.Object:
                {
                    var members = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty property in element.EnumerateObject())
                        members[property.Name] = ToClr(property.Value);
                    return members;
                }
                default:
                    return null;
            }
        }

        public bool TryGetString(string name, out string value)
        {
            return m_request.TryGetString(name, out value);
        }

        public string GetString(string name, string fallback = null)
        {
            return m_request.GetString(name, fallback);
        }

        public int GetInteger(string name, int fallback = 0)
        {
            return m_request.GetInteger(name, fallback);
        }

        public float GetFloat(string name, float fallback = 0f)
        {
            return m_request.GetFloat(name, fallback);
        }

        public bool GetBoolean(string name, bool fallback = false)
        {
            return m_request.GetBoolean(name, fallback);
        }

        public List<string> GetStringArray(string name)
        {
            return m_request.GetStringArray(name);
        }
    }

    /// <summary>扩展命令的处理函数：在**游戏线程**被调用（除非注册时关掉），返回值会被序列化成 JSON。</summary>
    public delegate object CmdBridgeCommandHandler(CmdBridgeCommandRequest request);

    /// <summary>一条已注册的扩展命令。</summary>
    internal sealed class CommandExtension
    {
        public string Name;

        /// <summary>注册者标识（通常是 Mod 名）：卸载时按它整批摘掉，防止"别的 Mod 的命令被误删"。</summary>
        public string Owner;

        public string Description;

        public bool RunOnGameThread = true;

        public CmdBridgeCommandHandler Handler;
    }

    /// <summary>
    /// 扩展命令注册表（线程安全）。
    ///
    /// 为什么要它：控制面一开始只有 CmdBridgeMod 自己的 obs./act./ui./focus. 命令，
    /// 但"操作 AI"这类命令属于上层 Mod（PlayerAiMod）。让上层去改 CmdBridgeMod 的路由代码是最差的耦合，
    /// 所以这里开一个注册口：**一套通道、一个 token、一个 CLI**，各 Mod 各自挂自己的命令前缀。
    /// </summary>
    internal sealed class CommandExtensionRegistry
    {
        private readonly Dictionary<string, CommandExtension> m_commands =
            new Dictionary<string, CommandExtension>(StringComparer.OrdinalIgnoreCase);

        private readonly object m_gate = new object();

        public int Count
        {
            get
            {
                lock (m_gate)
                    return m_commands.Count;
            }
        }

        public bool Register(string name, CmdBridgeCommandHandler handler, string owner,
            string description, bool runOnGameThread, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name) || handler == null)
            {
                error = "A command name and handler are required.";
                return false;
            }

            string key = name.Trim().ToLowerInvariant();
            lock (m_gate)
            {
                CommandExtension existing;
                if (m_commands.TryGetValue(key, out existing))
                {
                    error = "Command '" + key + "' is already registered by "
                        + (string.IsNullOrEmpty(existing.Owner) ? "<unknown>" : existing.Owner) + ".";
                    return false;
                }

                m_commands.Add(key, new CommandExtension
                {
                    Name = key,
                    Owner = owner,
                    Description = description,
                    RunOnGameThread = runOnGameThread,
                    Handler = handler
                });
                return true;
            }
        }

        /// <summary>摘下一条命令。只允许注册者本人（owner 相同）摘下，owner 为空表示强制摘。</summary>
        public bool Unregister(string name, string owner, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "A command name is required.";
                return false;
            }

            string key = name.Trim().ToLowerInvariant();
            lock (m_gate)
            {
                CommandExtension existing;
                if (!m_commands.TryGetValue(key, out existing))
                {
                    error = "Command '" + key + "' is not registered.";
                    return false;
                }
                if (!string.IsNullOrEmpty(owner)
                    && !string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    error = "Command '" + key + "' belongs to '"
                        + (existing.Owner ?? "<unknown>") + "'.";
                    return false;
                }
                m_commands.Remove(key);
                return true;
            }
        }

        /// <summary>按注册者整批摘掉（Mod 卸载时调用）。返回摘掉的条数。</summary>
        public int UnregisterAll(string owner)
        {
            if (string.IsNullOrEmpty(owner))
                return 0;

            var doomed = new List<string>();
            lock (m_gate)
            {
                foreach (KeyValuePair<string, CommandExtension> pair in m_commands)
                {
                    if (string.Equals(pair.Value.Owner, owner, StringComparison.Ordinal))
                        doomed.Add(pair.Key);
                }
                for (int i = 0; i < doomed.Count; i++)
                    m_commands.Remove(doomed[i]);
            }
            return doomed.Count;
        }

        public bool TryGet(string name, out CommandExtension extension)
        {
            extension = null;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            lock (m_gate)
            {
                return m_commands.TryGetValue(name.Trim(), out extension);
            }
        }

        public List<CommandExtension> List(string owner = null)
        {
            var result = new List<CommandExtension>();
            lock (m_gate)
            {
                foreach (CommandExtension extension in m_commands.Values)
                {
                    if (!string.IsNullOrEmpty(owner)
                        && !string.Equals(extension.Owner, owner, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    result.Add(extension);
                }
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }

        public List<string> Names(string owner = null)
        {
            List<CommandExtension> extensions = List(owner);
            var names = new List<string>(extensions.Count);
            for (int i = 0; i < extensions.Count; i++)
                names.Add(extensions[i].Name);
            return names;
        }
    }
}

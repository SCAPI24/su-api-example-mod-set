using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>
    /// 命令层的错误（带稳定错误码）。**故意不依赖 CmdBridgeMod 的类型**：
    /// 命令语义可以在没有游戏的临时工程里自检；到通道那一层（`AiCommandBridge`）再映射成
    /// CmdBridgeMod 的错误类型，客户端看到的错误码不变。
    /// </summary>
    public sealed class AiCommandException : Exception
    {
        public AiCommandException(string code, string message)
            : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? "command_failed" : code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// 控制面命令看到的请求（**不依赖 CmdBridgeMod 的类型**）。
    ///
    /// 参数统一按名字取（`sccmd raw ai.tree.load name=demo.greet`），
    /// 值来自 CmdBridgeMod 已经转好的 CLR 基元；也支持测试直接塞字典。
    /// </summary>
    public sealed class AiCommandRequest
    {
        private readonly Dictionary<string, object> m_args;

        public AiCommandRequest(string command, Dictionary<string, object> args = null)
        {
            Command = command;
            m_args = args ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>测试/内部构造便利：`Args("name", "demo.greet", "start", true)`。</summary>
        public static AiCommandRequest FromArgs(string command, params object[] namesAndValues)
        {
            var args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < namesAndValues.Length; i += 2)
                args[(string)namesAndValues[i]] = namesAndValues[i + 1];
            return new AiCommandRequest(command, args);
        }

        public string Command { get; }

        public IReadOnlyDictionary<string, object> Arguments
        {
            get { return m_args; }
        }

        public bool Has(string name)
        {
            return !string.IsNullOrEmpty(name) && m_args.ContainsKey(name)
                && m_args[name] != null;
        }

        public bool TryGetString(string name, out string value)
        {
            value = null;
            object raw;
            if (string.IsNullOrEmpty(name) || !m_args.TryGetValue(name, out raw) || raw == null)
                return false;
            value = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
            return value != null;
        }

        public string GetString(string name, string fallback = null)
        {
            string value;
            return TryGetString(name, out value) ? value : fallback;
        }

        public int GetInteger(string name, int fallback = 0)
        {
            object raw;
            if (!m_args.TryGetValue(name, out raw) || raw == null)
                return fallback;
            if (raw is long)
                return (int)(long)raw;
            if (raw is double)
                return (int)Math.Round((double)raw);
            if (raw is bool)
                return (bool)raw ? 1 : 0;
            int parsed;
            return int.TryParse(GetString(name, null), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        public float GetFloat(string name, float fallback = 0f)
        {
            object raw;
            if (!m_args.TryGetValue(name, out raw) || raw == null)
                return fallback;
            if (raw is long)
                return (long)raw;
            if (raw is double)
                return (float)(double)raw;
            float parsed;
            return float.TryParse(GetString(name, null), NumberStyles.Float,
                CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        public bool GetBoolean(string name, bool fallback = false)
        {
            object raw;
            if (!m_args.TryGetValue(name, out raw) || raw == null)
                return fallback;
            if (raw is bool)
                return (bool)raw;
            if (raw is long)
                return (long)raw != 0;
            bool parsed;
            return bool.TryParse(GetString(name, null), out parsed) ? parsed : fallback;
        }

        /// <summary>缺参报错用的统一入口（错误码 `invalid_argument`）。</summary>
        public string RequireString(string name)
        {
            string value = GetString(name, null);
            if (string.IsNullOrEmpty(value))
            {
                throw new AiCommandException("invalid_argument",
                    "'" + name + "' is required for " + Command + ".");
            }
            return value;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayerAiMod
{
    /// <summary>
    /// 动作原语（verb）的参数读取器。**不依赖游戏类型**：值来自包 JSON（<see cref="PackageValue"/>）
    /// 或测试直接塞的字典，两种来源都走这里，于是 verb 语义能在没有游戏的临时工程里逐条自检。
    ///
    /// 与 <see cref="PackageReader"/> 的关系：那个是"包格式"的读取器（带报告），
    /// 这个是"动作脚本一步"的读取器（带**错误码**，供 §4.12 的异常分支消费）。
    /// </summary>
    public sealed class ActionArgs
    {
        private readonly Dictionary<string, object> m_values =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public ActionArgs() { }

        public ActionArgs(Dictionary<string, object> values)
        {
            if (values == null)
                return;
            foreach (KeyValuePair<string, object> pair in values)
                m_values[pair.Key] = pair.Value;
        }

        public static ActionArgs FromValue(PackageValue value)
        {
            var args = new ActionArgs();
            if (value == null || !value.IsObject)
                return args;
            foreach (string name in value.MemberNames)
                args.m_values[name] = value.Get(name);
            return args;
        }

        public IReadOnlyDictionary<string, object> Values
        {
            get { return m_values; }
        }

        /// <summary>写入/覆盖一个参数（解析器逐字段填、测试直接塞值都用它）。</summary>
        public void Set(string name, object value)
        {
            if (string.IsNullOrEmpty(name))
                return;
            m_values[name] = value;
        }

        public bool Has(string name)
        {
            object raw;
            return !string.IsNullOrEmpty(name) && m_values.TryGetValue(name, out raw) && raw != null;
        }

        public IEnumerable<string> Names
        {
            get { return m_values.Keys; }
        }

        /// <summary>把未知参数名报出来（脚本写错属性名时不让它静默生效）。</summary>
        public List<string> UnknownNames(IEnumerable<ActionParamSpec> specs)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ActionParamSpec spec in specs)
                known.Add(spec.Name);

            var unknown = new List<string>();
            foreach (string name in m_values.Keys)
            {
                if (!known.Contains(name))
                    unknown.Add(name);
            }
            unknown.Sort(StringComparer.Ordinal);
            return unknown;
        }

        public bool TryGetString(string name, out string value)
        {
            value = null;
            object raw;
            if (!m_values.TryGetValue(name, out raw) || raw == null)
                return false;

            var packageValue = raw as PackageValue;
            if (packageValue != null)
            {
                if (packageValue.IsNull)
                    return false;
                value = packageValue.IsString ? packageValue.AsString() : packageValue.ToJson(false);
                return value != null;
            }

            value = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
            return value != null;
        }

        public string GetString(string name, string fallback = null)
        {
            string value;
            return TryGetString(name, out value) ? value : fallback;
        }

        public bool TryGetFloat(string name, out float value)
        {
            value = 0f;
            object raw;
            if (!m_values.TryGetValue(name, out raw) || raw == null)
                return false;

            var packageValue = raw as PackageValue;
            if (packageValue != null)
            {
                if (!packageValue.IsNumber)
                    return false;
                value = (float)packageValue.AsNumber();
                return true;
            }

            if (raw is bool)
            {
                value = (bool)raw ? 1f : 0f;
                return true;
            }
            if (raw is float)
            {
                value = (float)raw;
                return true;
            }
            if (raw is double)
            {
                value = (float)(double)raw;
                return true;
            }
            if (raw is int)
            {
                value = (int)raw;
                return true;
            }
            if (raw is long)
            {
                value = (long)raw;
                return true;
            }

            string text = raw as string;
            return text != null && float.TryParse(text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        public float GetFloat(string name, float fallback)
        {
            float value;
            return TryGetFloat(name, out value) ? value : fallback;
        }

        public int GetInt(string name, int fallback)
        {
            float value;
            if (!TryGetFloat(name, out value))
                return fallback;
            return (int)Math.Round(value);
        }

        public bool TryGetBool(string name, out bool value)
        {
            value = false;
            object raw;
            if (!m_values.TryGetValue(name, out raw) || raw == null)
                return false;

            var packageValue = raw as PackageValue;
            if (packageValue != null)
            {
                if (packageValue.IsBool)
                {
                    value = packageValue.AsBool();
                    return true;
                }
                if (packageValue.IsNumber)
                {
                    value = Math.Abs(packageValue.AsNumber()) > 0.0001;
                    return true;
                }
                return false;
            }

            if (raw is bool)
            {
                value = (bool)raw;
                return true;
            }
            if (raw is string)
                return bool.TryParse((string)raw, out value);
            if (raw is int)
            {
                value = (int)raw != 0;
                return true;
            }
            if (raw is long)
            {
                value = (long)raw != 0;
                return true;
            }
            if (raw is float)
            {
                value = Math.Abs((float)raw) > 0.0001f;
                return true;
            }
            if (raw is double)
            {
                value = Math.Abs((double)raw) > 0.0001;
                return true;
            }
            return false;
        }

        public bool GetBool(string name, bool fallback)
        {
            bool value;
            return TryGetBool(name, out value) ? value : fallback;
        }

        public override string ToString()
        {
            if (m_values.Count == 0)
                return "(no args)";
            var parts = new List<string>();
            foreach (KeyValuePair<string, object> pair in m_values)
            {
                var packageValue = pair.Value as PackageValue;
                parts.Add(pair.Key + "=" + (packageValue != null
                    ? packageValue.ToJson(false)
                    : Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(" ", parts);
        }
    }

    /// <summary>参数类型（决定校验与编辑器控件）。</summary>
    public enum ActionParamKind
    {
        Int,
        Float,
        Bool,
        String,
        Enum,
        /// <summary>毫秒时长：非负整数，且会被编译成帧数。</summary>
        DurationMs,
        /// <summary>快捷栏槽位 1..9。</summary>
        Slot,
        /// <summary>移动方向（八个方向 + 原地）。</summary>
        Direction
    }

    /// <summary>单个 verb 参数的规格。</summary>
    public sealed class ActionParamSpec
    {
        public ActionParamSpec(string name, ActionParamKind kind, bool required = false,
            string defaultValue = null, string[] allowed = null, string description = null,
            float minimum = float.NegativeInfinity, float maximum = float.PositiveInfinity)
        {
            Name = name;
            Kind = kind;
            Required = required;
            DefaultValue = defaultValue;
            Allowed = allowed;
            Description = description;
            Minimum = minimum;
            Maximum = maximum;
        }

        public string Name { get; }

        public ActionParamKind Kind { get; }

        public bool Required { get; }

        /// <summary>文本形式的默认值（编辑器展示用；真正的默认值在编译点）。</summary>
        public string DefaultValue { get; }

        public string[] Allowed { get; }

        public string Description { get; }

        public float Minimum { get; }

        public float Maximum { get; }

        public string Describe()
        {
            var text = new System.Text.StringBuilder();
            text.Append(Name).Append(':').Append(Kind.ToString().ToLowerInvariant());
            if (Required)
                text.Append(" required");
            if (!string.IsNullOrEmpty(DefaultValue))
                text.Append('=').Append(DefaultValue);
            if (Allowed != null && Allowed.Length > 0)
                text.Append(" [").Append(string.Join("|", Allowed)).Append(']');
            return text.ToString();
        }
    }

    /// <summary>
    /// verb 的错误（带稳定错误码，供行为树异常分支与 §4.12 的重取/拒包分流）。
    /// </summary>
    public sealed class ActionVerbException : Exception
    {
        public ActionVerbException(string code, string message) : base(message)
        {
            Code = string.IsNullOrEmpty(code) ? ActionErrorCodes.VerbFailed : code;
        }

        public string Code { get; }
    }

    /// <summary>动作层的稳定错误码（人、脚本、Laya 异常分支都按码判断，不看文案）。</summary>
    public static class ActionErrorCodes
    {
        /// <summary>verb 名不认识（脚本写错 / 版本不匹配）。</summary>
        public const string VerbUnknown = "verb_unknown";
        /// <summary>参数缺失、类型不符或越界。</summary>
        public const string InvalidArgument = "invalid_argument";
        /// <summary>执行器拒绝（未启用注入 / 键名不存在 / 引擎没接受）。</summary>
        public const string InjectRefused = "inject_refused";
        /// <summary>动作本身没问题，但没做到（挖不到、点不到、目标没了）。</summary>
        public const string StepFailed = "step_failed";
        /// <summary>执行超时（看门狗）。</summary>
        public const string Timeout = "timeout";
        /// <summary>守卫不满足（开局就拒绝执行）。</summary>
        public const string GuardFailed = "guard_failed";
        /// <summary>被外部中断（人夺回、更高优先级脚本、树被替换）。</summary>
        public const string Aborted = "aborted";
        /// <summary>兜底。</summary>
        public const string VerbFailed = "verb_failed";
    }
}

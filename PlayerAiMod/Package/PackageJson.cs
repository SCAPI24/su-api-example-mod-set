using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PlayerAiMod
{
    /// <summary>
    /// 包内 JSON 的解析与取值工具。
    ///
    /// 严格策略（计划 §4）：UTF-8、无 BOM、不允许注释与尾随逗号、深度与体积有上限。
    /// **只用 DOM API**（<c>JsonDocument</c> 一次性读成 <see cref="PackageValue"/>）：
    /// 不依赖反射序列化，AOT 下不会因为裁剪而炸。
    /// </summary>
    public static class PackageJson
    {
        /// <summary>单个 json 文件体积上限（manifest/tree 都是人写的，4 MB 足够）。</summary>
        public const int MaxBytes = 4 * 1024 * 1024;

        /// <summary>
        /// JSON 自身的嵌套深度上限。**必须明显大于树的深度上限**
        /// （树里一层节点 = JSON 里"对象 + children 数组"两层），
        /// 否则"树太深"会先被 JSON 解析器拦下，报出来的错就跟树无关了。
        /// </summary>
        public const int MaxDepth = 128;

        public static bool TryParseUtf8(byte[] bytes, string where, PackageReport report,
            out PackageValue value)
        {
            value = PackageValue.Null;
            if (bytes == null || bytes.Length == 0)
            {
                report.Error(PackageCodes.JsonInvalid, where, "json data is empty");
                return false;
            }
            if (bytes.Length > MaxBytes)
            {
                report.Error(PackageCodes.FileTooLarge, where,
                    "json data is " + bytes.Length + " bytes (limit " + MaxBytes + ")");
                return false;
            }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                report.Error(PackageCodes.JsonInvalid, where, "json must be UTF-8 without BOM");
                return false;
            }

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (Exception exception)
            {
                report.Error(PackageCodes.JsonInvalid, where, "json is not valid UTF-8: " + exception.Message);
                return false;
            }

            return TryParse(text, where, report, out value);
        }

        public static bool TryParse(string text, string where, PackageReport report,
            out PackageValue value)
        {
            value = PackageValue.Null;
            if (string.IsNullOrEmpty(text))
            {
                report.Error(PackageCodes.JsonInvalid, where, "json data is empty");
                return false;
            }

            JsonDocument document = null;
            try
            {
                document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxDepth
                });
                value = FromElement(document.RootElement, where, report, 0);
                return true;
            }
            catch (JsonException exception)
            {
                report.Error(PackageCodes.JsonInvalid, where, "invalid json: " + exception.Message);
                return false;
            }
            finally
            {
                document?.Dispose();
            }
        }

        private static PackageValue FromElement(JsonElement element, string where,
            PackageReport report, int depth)
        {
            if (depth > MaxDepth)
            {
                report.Error(PackageCodes.JsonTooDeep, where,
                    "json nesting exceeds " + MaxDepth + " levels");
                return PackageValue.Null;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    PackageValue result = PackageValue.Object();
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (result.Has(property.Name))
                        {
                            report.Warn(PackageCodes.JsonInvalid, where,
                                "duplicate key '" + property.Name + "' (the last one wins)");
                        }
                        result.Set(property.Name,
                            FromElement(property.Value, where, report, depth + 1));
                    }
                    return result;
                }
                case JsonValueKind.Array:
                {
                    PackageValue result = PackageValue.Array();
                    foreach (JsonElement item in element.EnumerateArray())
                        result.Add(FromElement(item, where, report, depth + 1));
                    return result;
                }
                case JsonValueKind.String:
                    return PackageValue.Str(element.GetString());
                case JsonValueKind.Number:
                    return PackageValue.Number(element.GetDouble());
                case JsonValueKind.True:
                    return PackageValue.Bool(true);
                case JsonValueKind.False:
                    return PackageValue.Bool(false);
                default:
                    return PackageValue.Null;
            }
        }

        /// <summary>
        /// 标识符合法性：包 id、引用 id、节点 id 共用同一条规则
        /// （字母/数字/点/下划线/连字符，1..64 字符）。禁止空白与路径分隔符，
        /// 这样 id 可以直接出现在路径、日志与命令里而不会被误解。
        /// </summary>
        public static bool IsValidIdentifier(string value, int maxLength = 64)
        {
            if (string.IsNullOrEmpty(value) || value.Length > maxLength)
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool ok = (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '.' || character == '_' || character == '-';
                if (!ok)
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 带错误上报的字段读取器：所有"读一个已知字段"的代码都走它，
    /// 于是错误文案、类型不符的处理、未识别字段的提示只有一份实现。
    /// </summary>
    public sealed class PackageReader
    {
        private readonly PackageValue m_owner;
        private readonly PackageReport m_report;
        private readonly HashSet<string> m_consumed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public PackageReader(PackageValue owner, string where, PackageReport report,
            string unknownCode = PackageCodes.PropertyUnknown)
        {
            m_owner = owner ?? PackageValue.Null;
            Where = where ?? string.Empty;
            m_report = report ?? new PackageReport();
            UnknownCode = unknownCode;
        }

        public string Where { get; private set; }

        public string UnknownCode { get; }

        public PackageReport Report
        {
            get { return m_report; }
        }

        public PackageValue Owner
        {
            get { return m_owner; }
        }

        /// <summary>把位置前缀换成更细的位置（节点 id 变化时用）。</summary>
        public void SetWhere(string where)
        {
            Where = where ?? string.Empty;
        }

        public PackageValue Raw(string name)
        {
            m_consumed.Add(name);
            return m_owner.Get(name);
        }

        public bool Has(string name)
        {
            return m_owner.Has(name);
        }

        public string Str(string name, string fallback)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return fallback;
            if (!value.IsString)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected string, found " + value.DescribeKind() + " (" + value.Preview(40) + ")");
                return fallback;
            }
            return value.AsString(fallback);
        }

        public string RequiredStr(string name, string fallback = null)
        {
            string value = Str(name, fallback);
            if (string.IsNullOrEmpty(value))
            {
                m_report.Error(PackageCodes.PropertyRequired, Where + "." + name,
                    "required string property is missing or empty");
            }
            return value;
        }

        public bool Bool(string name, bool fallback)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return fallback;
            if (!value.IsBool)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected bool, found " + value.DescribeKind() + " (" + value.Preview(40) + ")");
                return fallback;
            }
            return value.AsBool(fallback);
        }

        public int Int(string name, int fallback)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return fallback;
            if (!value.IsNumber)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected number, found " + value.DescribeKind() + " (" + value.Preview(40) + ")");
                return fallback;
            }
            return value.AsInt(fallback);
        }

        public float Float(string name, float fallback)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return fallback;
            if (!value.IsNumber)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected number, found " + value.DescribeKind() + " (" + value.Preview(40) + ")");
                return fallback;
            }
            return value.AsFloat(fallback);
        }

        /// <summary>
        /// 枚举读取：大小写不敏感命中候选值，返回**规范拼写**；未命中报错并返回默认值。
        /// </summary>
        public string Enum(string name, string fallback, string[] allowed)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return fallback;
            if (!value.IsString)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected enum string, found " + value.DescribeKind() + " (" + value.Preview(40) + ")");
                return fallback;
            }

            string text = value.AsString();
            for (int i = 0; i < allowed.Length; i++)
            {
                if (string.Equals(allowed[i], text, StringComparison.OrdinalIgnoreCase))
                    return allowed[i];
            }

            m_report.Error(PackageCodes.PropertyEnum, Where + "." + name,
                "'" + text + "' is not one of " + string.Join("|", allowed));
            return fallback;
        }

        /// <summary>字符串数组（也接受单个字符串，方便手写包）。</summary>
        public List<string> StringList(string name)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            var result = new List<string>();
            if (value.IsNull)
                return result;

            if (value.IsString)
            {
                result.Add(value.AsString());
                return result;
            }

            if (!value.IsArray)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected string array, found " + value.DescribeKind());
                return result;
            }

            for (int i = 0; i < value.Count; i++)
            {
                PackageValue item = value.Item(i);
                if (!item.IsString)
                {
                    m_report.Error(PackageCodes.PropertyKind, Where + "." + name + "[" + i + "]",
                        "expected string, found " + item.DescribeKind());
                    continue;
                }
                result.Add(item.AsString());
            }
            return result;
        }

        /// <summary>对象字段（缺失时返回空对象；类型不符报错）。</summary>
        public PackageValue ObjectField(string name)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return PackageValue.Object();
            if (!value.IsObject)
            {
                m_report.Error(PackageCodes.PropertyKind, Where + "." + name,
                    "expected object, found " + value.DescribeKind());
                return PackageValue.Object();
            }
            return value;
        }

        /// <summary>数组字段（缺失时返回空数组；类型不符报错）。</summary>
        public PackageValue ArrayField(string name, string code = PackageCodes.PropertyKind)
        {
            m_consumed.Add(name);
            PackageValue value = m_owner.Get(name);
            if (value.IsNull)
                return PackageValue.Array();
            if (!value.IsArray)
            {
                m_report.Error(code, Where + "." + name,
                    "expected array, found " + value.DescribeKind());
                return PackageValue.Array();
            }
            return value;
        }

        /// <summary>
        /// 未被读取过的字段一律提示：手写包最常见的问题就是拼错属性名（"timeout" 写成 "timeOut"），
        /// 静默忽略会让人查半天。
        /// </summary>
        public void ReportUnknown()
        {
            foreach (string name in m_owner.MemberNames)
            {
                if (m_consumed.Contains(name))
                    continue;
                m_report.Warn(UnknownCode, Where + "." + name,
                    "unknown field '" + name + "' (ignored; check spelling)");
            }
        }
    }
}

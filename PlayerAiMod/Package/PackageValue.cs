using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PlayerAiMod
{
    /// <summary>包内 JSON 值的种类。</summary>
    public enum PackageValueKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// 轻量 JSON 值模型 —— 包格式的**唯一数据载体**。
    ///
    /// 为什么不用 <c>JsonElement</c>：它的生命周期绑定在 <c>JsonDocument</c> 上，
    /// 而加载器读完 zip 就要立刻释放文档与句柄（计划 §5「不锁文件」）；
    /// 这份自持有的值模型让"解析完就关文件"成为可能，也让内存态改写（§4.5）与导出（§4.5）
    /// 有统一的表示。写入走 <see cref="Utf8JsonWriter"/>（AOT 安全，不依赖反射序列化）。
    /// </summary>
    public sealed class PackageValue
    {
        private readonly bool m_bool;
        private readonly double m_number;
        private readonly string m_string;
        private readonly List<PackageValue> m_items;
        private readonly Dictionary<string, PackageValue> m_members;

        private PackageValue(PackageValueKind kind, bool boolValue, double number,
            string text, List<PackageValue> items, Dictionary<string, PackageValue> members)
        {
            Kind = kind;
            m_bool = boolValue;
            m_number = number;
            m_string = text;
            m_items = items;
            m_members = members;
        }

        public PackageValueKind Kind { get; }

        public static readonly PackageValue Null =
            new PackageValue(PackageValueKind.Null, false, 0.0, null, null, null);

        public static PackageValue Bool(bool value)
        {
            return new PackageValue(PackageValueKind.Bool, value, 0.0, null, null, null);
        }

        public static PackageValue Number(double value)
        {
            return new PackageValue(PackageValueKind.Number, false, value, null, null, null);
        }

        public static PackageValue Number(float value)
        {
            return Number((double)value);
        }

        public static PackageValue Str(string value)
        {
            return value == null
                ? Null
                : new PackageValue(PackageValueKind.String, false, 0.0, value, null, null);
        }

        public static PackageValue Array()
        {
            return new PackageValue(PackageValueKind.Array, false, 0.0, null,
                new List<PackageValue>(), null);
        }

        public static PackageValue Array(IEnumerable<PackageValue> items)
        {
            var list = new List<PackageValue>();
            if (items != null)
                list.AddRange(items);
            return new PackageValue(PackageValueKind.Array, false, 0.0, null, list, null);
        }

        public static PackageValue Object()
        {
            return new PackageValue(PackageValueKind.Object, false, 0.0, null, null,
                new Dictionary<string, PackageValue>(StringComparer.Ordinal));
        }

        // ---------------------------------------------------------------- 判定

        public bool IsNull
        {
            get { return Kind == PackageValueKind.Null; }
        }

        public bool IsBool
        {
            get { return Kind == PackageValueKind.Bool; }
        }

        public bool IsNumber
        {
            get { return Kind == PackageValueKind.Number; }
        }

        public bool IsString
        {
            get { return Kind == PackageValueKind.String; }
        }

        public bool IsArray
        {
            get { return Kind == PackageValueKind.Array; }
        }

        public bool IsObject
        {
            get { return Kind == PackageValueKind.Object; }
        }

        /// <summary>数组元素个数；对象成员个数；其它类型为 0。</summary>
        public int Count
        {
            get
            {
                if (m_items != null)
                    return m_items.Count;
                if (m_members != null)
                    return m_members.Count;
                return 0;
            }
        }

        public IReadOnlyList<PackageValue> Items
        {
            get { return m_items ?? (IReadOnlyList<PackageValue>)EmptyItems; }
        }

        private static readonly PackageValue[] EmptyItems = new PackageValue[0];

        public ICollection<string> MemberNames
        {
            get
            {
                return m_members != null
                    ? (ICollection<string>)m_members.Keys
                    : EmptyNames;
            }
        }

        private static readonly string[] EmptyNames = new string[0];

        // ---------------------------------------------------------------- 取值

        public bool AsBool(bool fallback = false)
        {
            if (Kind == PackageValueKind.Bool)
                return m_bool;
            if (Kind == PackageValueKind.Number)
                return Math.Abs(m_number) > double.Epsilon;
            return fallback;
        }

        public double AsNumber(double fallback = 0.0)
        {
            return Kind == PackageValueKind.Number ? m_number : fallback;
        }

        public float AsFloat(float fallback = 0f)
        {
            return Kind == PackageValueKind.Number ? (float)m_number : fallback;
        }

        public int AsInt(int fallback = 0)
        {
            if (Kind != PackageValueKind.Number)
                return fallback;
            double rounded = Math.Round(m_number, MidpointRounding.AwayFromZero);
            if (rounded > int.MaxValue || rounded < int.MinValue)
                return fallback;
            return (int)rounded;
        }

        public string AsString(string fallback = null)
        {
            return Kind == PackageValueKind.String ? m_string : fallback;
        }

        public PackageValue Get(string name)
        {
            PackageValue value;
            return TryGet(name, out value) ? value : Null;
        }

        public bool TryGet(string name, out PackageValue value)
        {
            value = Null;
            if (m_members == null || string.IsNullOrEmpty(name))
                return false;
            return m_members.TryGetValue(name, out value) && value != null;
        }

        public bool Has(string name)
        {
            return m_members != null && !string.IsNullOrEmpty(name) && m_members.ContainsKey(name);
        }

        public PackageValue Item(int index)
        {
            if (m_items == null || index < 0 || index >= m_items.Count)
                return Null;
            return m_items[index];
        }

        // ---------------------------------------------------------------- 改写（内存态用）

        /// <summary>对象成员写入；非对象返回 false（不做隐式升级）。</summary>
        public bool Set(string name, PackageValue value)
        {
            if (m_members == null || string.IsNullOrEmpty(name))
                return false;
            m_members[name] = value ?? Null;
            return true;
        }

        public bool Remove(string name)
        {
            return m_members != null && !string.IsNullOrEmpty(name) && m_members.Remove(name);
        }

        public bool Add(PackageValue value)
        {
            if (m_items == null)
                return false;
            m_items.Add(value ?? Null);
            return true;
        }

        /// <summary>深拷贝（内存态改写与导出时避免共享引用）。</summary>
        public PackageValue DeepClone()
        {
            switch (Kind)
            {
                case PackageValueKind.Array:
                {
                    var clone = Array();
                    for (int i = 0; i < m_items.Count; i++)
                        clone.Add(m_items[i].DeepClone());
                    return clone;
                }
                case PackageValueKind.Object:
                {
                    var clone = Object();
                    foreach (KeyValuePair<string, PackageValue> pair in m_members)
                        clone.Set(pair.Key, pair.Value.DeepClone());
                    return clone;
                }
                default:
                    return this;
            }
        }

        // ---------------------------------------------------------------- 输出

        /// <summary>紧凑 JSON（单行），用于日志与错误信息。</summary>
        public string ToJson(bool indented = false)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = indented,
                SkipValidation = false
            }))
            {
                Write(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public void Write(Utf8JsonWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            switch (Kind)
            {
                case PackageValueKind.Null:
                    writer.WriteNullValue();
                    break;
                case PackageValueKind.Bool:
                    writer.WriteBooleanValue(m_bool);
                    break;
                case PackageValueKind.Number:
                    WriteNumber(writer, m_number);
                    break;
                case PackageValueKind.String:
                    writer.WriteStringValue(m_string ?? string.Empty);
                    break;
                case PackageValueKind.Array:
                    writer.WriteStartArray();
                    for (int i = 0; i < m_items.Count; i++)
                        m_items[i].Write(writer);
                    writer.WriteEndArray();
                    break;
                default:
                    writer.WriteStartObject();
                    foreach (KeyValuePair<string, PackageValue> pair in m_members)
                    {
                        writer.WritePropertyName(pair.Key);
                        pair.Value.Write(writer);
                    }
                    writer.WriteEndObject();
                    break;
            }
        }

        private static void WriteNumber(Utf8JsonWriter writer, double value)
        {
            // 整数原样写，避免 "1" 变成 "1.0"（编辑器与人工读包时更自然）
            if (value == Math.Floor(value) && value >= -9.007199254740992E15
                && value <= 9.007199254740992E15)
            {
                writer.WriteNumberValue((long)value);
            }
            else
            {
                writer.WriteNumberValue(value);
            }
        }

        /// <summary>短描述（错误信息里标明"这里期望的是对象，实际是数组"之类）。</summary>
        public string DescribeKind()
        {
            return Kind.ToString().ToLowerInvariant();
        }

        /// <summary>紧凑预览，最多 <paramref name="maxLength"/> 个字符。</summary>
        public string Preview(int maxLength = 80)
        {
            string text;
            try
            {
                text = ToJson();
            }
            catch (Exception)
            {
                text = "<" + DescribeKind() + ">";
            }
            if (text.Length <= maxLength)
                return text;
            return text.Substring(0, maxLength) + "...";
        }

        public override string ToString()
        {
            return Preview();
        }

        /// <summary>按不变文化格式化浮点（包与日志里统一用 "."，不受系统区域影响）。</summary>
        public static string FormatNumber(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}

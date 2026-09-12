using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PlayerAiMod.Editor
{
    /// <summary>
    /// 极简 JSON 写出（字典/列表/基元/PackageValue）。
    /// 不用反射序列化：编辑器返回的结构都是自己构造的字典，手写更可控、也没有 AOT/裁剪问题。
    /// </summary>
    internal static class JsonWriter
    {
        public static string Write(object value, bool indented = true)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value, indented, 0, 16);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value, bool indented,
            int depth, int budget)
        {
            if (budget <= 0)
            {
                builder.Append("null");
                return;
            }

            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is PackageValue)
            {
                builder.Append(((PackageValue)value).ToJson(indented));
                return;
            }

            if (value is string)
            {
                WriteString(builder, (string)value);
                return;
            }

            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is int || value is long || value is short || value is byte)
            {
                builder.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is float || value is double || value is decimal)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    builder.Append("null");
                else if (number == Math.Floor(number) && Math.Abs(number) < 9.007199254740992E15)
                    builder.Append(((long)number).ToString(CultureInfo.InvariantCulture));
                else
                    builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                WriteObject(builder, dictionary, indented, depth, budget);
                return;
            }

            var map = value as IDictionary;
            if (map != null)
            {
                var converted = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in map)
                    converted[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = entry.Value;
                WriteObject(builder, converted, indented, depth, budget);
                return;
            }

            var list = value as IEnumerable;
            if (list != null)
            {
                WriteArray(builder, list, indented, depth, budget);
                return;
            }

            WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteObject(StringBuilder builder, IDictionary<string, object> map,
            bool indented, int depth, int budget)
        {
            builder.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> pair in map)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                NewLine(builder, indented, depth + 1);
                WriteString(builder, pair.Key);
                builder.Append(':');
                if (indented)
                    builder.Append(' ');
                WriteValue(builder, pair.Value, indented, depth + 1, budget - 1);
            }
            if (!first)
                NewLine(builder, indented, depth);
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, IEnumerable items, bool indented,
            int depth, int budget)
        {
            builder.Append('[');
            bool first = true;
            foreach (object item in items)
            {
                if (!first)
                    builder.Append(',');
                first = false;
                NewLine(builder, indented, depth + 1);
                WriteValue(builder, item, indented, depth + 1, budget - 1);
            }
            if (!first)
                NewLine(builder, indented, depth);
            builder.Append(']');
        }

        private static void NewLine(StringBuilder builder, bool indented, int depth)
        {
            if (!indented)
                return;
            builder.Append('\n');
            builder.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            if (text != null)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 0x20 || c > 0x7E)
                                builder.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                builder.Append(c);
                            break;
                    }
                }
            }
            builder.Append('"');
        }
    }
}

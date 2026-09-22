using Engine;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ScMultiplayer
{
    /// <summary>
    /// 通用「世界设置」数据修改（**自定义 operation**，不是内置 `ScMP.Player.*`）。
    ///
    /// 设计要点（按需求）：
    ///   · 联机 mod **不认识"季节"**：它只做"把 WorldSettings 里的字段改成给定值"这件通用的事，
    ///     具体改什么由客户端送来的字段名决定（例：`TimeOfYear`＝季节/日期）。
    ///   · `WorldSettings` 当成一个**文件**看待：所有简单字段都能改（反射 + 类型化解析），
    ///     与引擎 `WorldSettings.Load(ValuesDictionary)` 读 Project.xml 的口径一致。
    ///   · **落地与分发都在主机**：主机审批后自己写 `WorldSettings`，再由主机通过世界信息广播
    ///     把整份设置快照发给**所有客户端**，客户端只是应用 —— 任何 mod（含 GmMod）都不落地、不分发。
    ///   · 载荷是**纯文本**（每行 `字段名\t值`），不用 JSON：本 mod 会混淆改名，
    ///     JSON 依赖属性名会静默解析失败（实测把季节写成了夏至）。
    /// </summary>
    internal static class WorldSettingsDataOperation
    {
        /// <summary>自定义 operation 名（名字里没有季节，联机 mod 不知道季节是什么）。</summary>
        public const string Name = "ScMP.Data.WorldSettings";

        public static bool IsWorldSettingsOperation(string operation) =>
            string.Equals(operation, Name, StringComparison.Ordinal);
    }

    /// <summary>`ScMP.Data.WorldSettings` 的纯文本载荷编解码。</summary>
    internal static class WorldSettingsDataCodec
    {
        public const int MaximumEntries = 64;
        public const int MaximumNameLength = 64;
        public const int MaximumValueLength = 256;

        public static byte[] Encode(IEnumerable<KeyValuePair<string, string>> entries)
        {
            var builder = new StringBuilder();
            if (entries != null)
            {
                foreach (KeyValuePair<string, string> entry in entries)
                {
                    builder.Append(Sanitize(entry.Key, MaximumNameLength));
                    builder.Append('\t');
                    builder.Append(Sanitize(entry.Value, MaximumValueLength));
                    builder.Append('\n');
                }
            }
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        public static bool TryDecode(byte[] payload,
            out List<KeyValuePair<string, string>> entries, out string error)
        {
            entries = new List<KeyValuePair<string, string>>();
            error = null;
            if (payload == null || payload.Length == 0 || payload.Length > 16 * 1024)
            {
                error = "World settings payload must contain 1-16384 bytes.";
                return false;
            }
            string text;
            try
            {
                text = Encoding.UTF8.GetString(payload);
            }
            catch (Exception)
            {
                error = "World settings payload is not valid UTF-8.";
                return false;
            }
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                    continue;
                if (entries.Count >= MaximumEntries)
                {
                    error = "World settings payload has too many entries.";
                    return false;
                }
                int separator = line.IndexOf('\t');
                if (separator <= 0)
                {
                    error = "World settings entry must be 'Name<TAB>Value'.";
                    return false;
                }
                string name = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (name.Length == 0 || name.Length > MaximumNameLength ||
                    value.Length > MaximumValueLength)
                {
                    error = "World settings entry is out of range.";
                    return false;
                }
                entries.Add(new KeyValuePair<string, string>(name, value));
            }
            if (entries.Count == 0)
            {
                error = "World settings payload is empty.";
                return false;
            }
            return true;
        }

        private static string Sanitize(string text, int maximumLength)
        {
            string value = (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ')
                .Replace('\n', ' ');
            return value.Length > maximumLength ? value.Substring(0, maximumLength) : value;
        }
    }

    /// <summary>
    /// 通用「地图方块」数据修改（**自定义 operation**，与 `ScMP.Data.WorldSettings` 同一层）。
    ///
    /// 载荷：**纯文本，每行 `x,y,z,contents[,data]`**（UTF-8）。contents = 方块内容号，
    /// data = 方块数据位（省略按 0）。光照位一律由**主机**按原地形取，客户端不能指定光源。
    /// 主机落地后由既有地形同步链广播给所有客户端（分发在主机侧）。
    /// </summary>
    internal static class CellDataOperation
    {
        public const string Name = "ScMP.Data.Cells";

        public static bool IsCellOperation(string operation) =>
            string.Equals(operation, Name, StringComparison.Ordinal);
    }

    internal static class CellDataCodec
    {
        public const int MaximumEntries = 256;
        private const int MaximumCoordinateMagnitude = 1000000;
        private const int MaximumContents = 1023;
        private const int MaximumDataBits = 0xFFFF;

        internal struct CellEdit
        {
            public int X;
            public int Y;
            public int Z;
            public int Contents;
            public int Data;
        }

        public static bool TryDecode(byte[] payload, out List<CellEdit> edits, out string error)
        {
            edits = new List<CellEdit>();
            error = null;
            if (payload == null || payload.Length == 0 || payload.Length > 32 * 1024)
            {
                error = "Cell payload must contain 1-32768 bytes.";
                return false;
            }
            foreach (string rawLine in Encoding.UTF8.GetString(payload).Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;
                if (edits.Count >= MaximumEntries)
                {
                    error = "Cell payload has too many entries.";
                    return false;
                }
                string[] parts = line.Split(',');
                if (parts.Length < 4)
                {
                    error = "Cell entry must be 'x,y,z,contents[,data]'.";
                    return false;
                }
                if (!TryParseInt(parts[0], out int x) || !TryParseInt(parts[1], out int y) ||
                    !TryParseInt(parts[2], out int z) || !TryParseInt(parts[3], out int contents))
                {
                    error = "Cell entry must be 'x,y,z,contents[,data]'.";
                    return false;
                }
                int data = 0;
                if (parts.Length >= 5 && !TryParseInt(parts[4], out data))
                {
                    error = "Cell entry must be 'x,y,z,contents[,data]'.";
                    return false;
                }
                if (MathUtils.Abs(x) > MaximumCoordinateMagnitude ||
                    MathUtils.Abs(z) > MaximumCoordinateMagnitude ||
                    y < 0 || y > 511 ||
                    contents < 0 || contents > MaximumContents ||
                    data < 0 || data > MaximumDataBits)
                {
                    error = "Cell entry is out of range.";
                    return false;
                }
                edits.Add(new CellEdit { X = x, Y = y, Z = z, Contents = contents, Data = data });
            }
            if (edits.Count == 0)
            {
                error = "Cell payload is empty.";
                return false;
            }
            return true;
        }

        private static bool TryParseInt(string text, out int value) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out value);
    }

    /// <summary>
    /// `WorldSettings` 的字段级读写：主机落地（客户端请求的字段）与快照分发（主机→全端）。
    /// 解析口径与引擎 `WorldSettings.Load` 一致（按字段类型解析，InvariantCulture）。
    /// </summary>
    internal static class WorldSettingsFields
    {
        private const float MaximumFloatMagnitude = 1e6f;
        private const int MaximumIntegerMagnitude = 1000000;
        private const int MaximumStringLength = 256;

        private static readonly BindingFlags FieldFlags =
            BindingFlags.Public | BindingFlags.Instance;

        /// <summary>把客户端请求的字段写进 `settings`；返回是否至少改了一项。</summary>
        public static bool TryApply(WorldSettings settings,
            List<KeyValuePair<string, string>> entries, out string summary, out string error)
        {
            summary = null;
            error = null;
            if (settings == null)
            {
                error = "The world settings are unavailable.";
                return false;
            }
            if (entries == null || entries.Count == 0)
            {
                error = "The world settings request is empty.";
                return false;
            }
            var changed = new List<string>();
            foreach (KeyValuePair<string, string> entry in entries)
            {
                FieldInfo field = typeof(WorldSettings).GetField(entry.Key, FieldFlags);
                if (field == null)
                {
                    error = "Unknown world setting '" + entry.Key + "'.";
                    return false;
                }
                if (!TryConvert(field.FieldType, entry.Value, out object converted))
                {
                    error = "World setting '" + entry.Key + "' does not accept '" +
                        entry.Value + "'.";
                    return false;
                }
                field.SetValue(settings, converted);
                changed.Add(field.Name + "=" + Format(converted, field.FieldType));
            }
            summary = FormatSummary(changed);
            return changed.Count > 0;
        }

        /// <summary>主机侧快照：所有可表达为文本的字段（用于广播给全端）。</summary>
        public static Dictionary<string, string> Capture(WorldSettings settings)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            if (settings == null)
                return snapshot;
            foreach (FieldInfo field in typeof(WorldSettings).GetFields(FieldFlags))
            {
                if (!IsSimple(field.FieldType))
                    continue;
                snapshot[field.Name] = Format(field.GetValue(settings), field.FieldType);
            }
            return snapshot;
        }

        /// <summary>客户端侧应用主机快照：只写有差异的字段，解析失败的单字段跳过。</summary>
        public static bool TryApplySnapshot(WorldSettings settings, IDictionary<string, string> snapshot)
        {
            if (settings == null || snapshot == null || snapshot.Count == 0)
                return false;
            var changed = new List<string>();
            foreach (KeyValuePair<string, string> entry in snapshot)
            {
                FieldInfo field = typeof(WorldSettings).GetField(entry.Key, FieldFlags);
                if (field == null || !IsSimple(field.FieldType))
                    continue;
                string current = Format(field.GetValue(settings), field.FieldType);
                if (string.Equals(current, entry.Value, StringComparison.Ordinal))
                    continue;
                if (!TryConvert(field.FieldType, entry.Value, out object converted))
                    continue;
                field.SetValue(settings, converted);
                changed.Add(field.Name + "=" + Format(converted, field.FieldType));
            }
            if (changed.Count == 0)
                return false;
            Log.Information("[ScMP] Applied host world settings: " + FormatSummary(changed));
            return true;
        }

        private static string FormatSummary(List<string> changed)
        {
            if (changed == null || changed.Count == 0)
                return string.Empty;
            string head = string.Join(", ", changed.Take(3));
            return changed.Count > 3 ? head + " (+" + (changed.Count - 3) + " more)" : head;
        }

        private static bool IsSimple(Type type) =>
            type == typeof(float) || type == typeof(int) || type == typeof(bool) ||
            type == typeof(string) || type == typeof(Vector2) || type.IsEnum;

        private static bool TryConvert(Type type, string text, out object value)
        {
            value = null;
            if (text == null)
                return false;
            string trimmed = text.Trim();
            if (type == typeof(float))
            {
                if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float f) || !IsFinite(f) || MathUtils.Abs(f) > MaximumFloatMagnitude)
                    return false;
                value = f;
                return true;
            }
            if (type == typeof(int))
            {
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int i) || MathUtils.Abs(i) > MaximumIntegerMagnitude)
                    return false;
                value = i;
                return true;
            }
            if (type == typeof(bool))
            {
                if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
                    trimmed == "1")
                {
                    value = true;
                    return true;
                }
                if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) ||
                    trimmed == "0")
                {
                    value = false;
                    return true;
                }
                return false;
            }
            if (type == typeof(string))
            {
                if (trimmed.Length > MaximumStringLength)
                    return false;
                value = trimmed;
                return true;
            }
            if (type == typeof(Vector2))
            {
                string[] parts = trimmed.Split(',');
                if (parts.Length != 2 ||
                    !float.TryParse(parts[0].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(parts[1].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float y) ||
                    !IsFinite(x) || !IsFinite(y) ||
                    MathUtils.Abs(x) > MaximumFloatMagnitude || MathUtils.Abs(y) > MaximumFloatMagnitude)
                    return false;
                value = new Vector2(x, y);
                return true;
            }
            if (type.IsEnum)
            {
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int raw))
                {
                    if (!Enum.IsDefined(type, raw))
                        return false;
                    value = Enum.ToObject(type, raw);
                    return true;
                }
                try
                {
                    value = Enum.Parse(type, trimmed, ignoreCase: true);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            return false;
        }

        private static string Format(object value, Type type)
        {
            if (value == null)
                return string.Empty;
            if (type == typeof(float))
                return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (type == typeof(int))
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (type == typeof(bool))
                return (bool)value ? "true" : "false";
            if (type == typeof(Vector2))
            {
                Vector2 vector = (Vector2)value;
                return vector.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                    vector.Y.ToString("R", CultureInfo.InvariantCulture);
            }
            if (type.IsEnum)
                return value.ToString();
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

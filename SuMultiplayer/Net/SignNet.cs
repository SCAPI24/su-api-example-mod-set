using System;
using System.Collections.Generic;
using System.Globalization;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>One sign's authoritative text state (pure value; no Game types).</summary>
    public sealed class SignRecordData
    {
        public const int LineCount = 4;
        public const int MaxLineLength = 256;
        public const int MaxUrlLength = 2048;

        public SignRecordData(int x, int y, int z, string[] lines, uint[] colors, string url)
        {
            X = x;
            Y = y;
            Z = z;
            Lines = new string[LineCount];
            Colors = new uint[LineCount];
            for (int i = 0; i < LineCount; i++)
            {
                Lines[i] = lines != null && i < lines.Length ? (lines[i] ?? string.Empty) : string.Empty;
                Colors[i] = colors != null && i < colors.Length ? colors[i] : 0u;
            }
            Url = url ?? string.Empty;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public string[] Lines { get; }
        public uint[] Colors { get; }
        public string Url { get; }

        public bool SameContent(SignRecordData other)
        {
            if (other == null || X != other.X || Y != other.Y || Z != other.Z) return false;
            if (!string.Equals(Url, other.Url, StringComparison.Ordinal)) return false;
            for (int i = 0; i < LineCount; i++)
            {
                if (!string.Equals(Lines[i], other.Lines[i], StringComparison.Ordinal)) return false;
                if (Colors[i] != other.Colors[i]) return false;
            }
            return true;
        }

        public string Describe() =>
            "(" + X.ToString(CultureInfo.InvariantCulture) + "," +
            Y.ToString(CultureInfo.InvariantCulture) + "," +
            Z.ToString(CultureInfo.InvariantCulture) + ") \"" +
            (Lines[0] ?? string.Empty) + "\"";
    }

    /// <summary>Sign protobuf codec + snapshot equality (pure logic, no Game).</summary>
    public static class SignCodec
    {
        public const int MaxSigns = 65536;
        private const int MaxPayloadBytes = 32 * 1024;

        public static P.SignRecord ToProto(SignRecordData record)
        {
            var message = new P.SignRecord
            {
                X = record.X,
                Y = record.Y,
                Z = record.Z,
                Url = record.Url ?? string.Empty
            };
            for (int i = 0; i < SignRecordData.LineCount; i++)
            {
                message.Lines.Add(record.Lines[i] ?? string.Empty);
                message.Colors.Add(record.Colors[i]);
            }
            return message;
        }

        public static bool TryFromProto(P.SignRecord message, out SignRecordData record,
            out string reason)
        {
            record = null;
            reason = null;
            if (message == null)
            {
                reason = "record-null";
                return false;
            }
            if (message.Lines.Count != SignRecordData.LineCount ||
                message.Colors.Count != SignRecordData.LineCount)
            {
                reason = "line-count:" + message.Lines.Count + "/" + message.Colors.Count;
                return false;
            }
            var lines = new string[SignRecordData.LineCount];
            var colors = new uint[SignRecordData.LineCount];
            for (int i = 0; i < SignRecordData.LineCount; i++)
            {
                string line = message.Lines[i] ?? string.Empty;
                if (line.Length > SignRecordData.MaxLineLength)
                {
                    reason = "line-too-long:" + line.Length;
                    return false;
                }
                lines[i] = line;
                colors[i] = message.Colors[i];
            }
            string url = message.Url ?? string.Empty;
            if (url.Length > SignRecordData.MaxUrlLength)
            {
                reason = "url-too-long:" + url.Length;
                return false;
            }
            record = new SignRecordData(message.X, message.Y, message.Z, lines, colors, url);
            return true;
        }

        public static bool TryEncodeState(int revision, bool full, string reason,
            IReadOnlyList<SignRecordData> signs, out P.SignState message, out string error)
        {
            message = null;
            error = null;
            if (signs != null && signs.Count > MaxSigns)
            {
                error = "state-too-large:" + signs.Count;
                return false;
            }
            var encoded = new P.SignState
            {
                Revision = revision,
                Full = full,
                Reason = reason ?? string.Empty
            };
            if (signs != null)
                for (int i = 0; i < signs.Count; i++)
                    if (signs[i] != null) encoded.Signs.Add(ToProto(signs[i]));
            message = encoded;
            return true;
        }

        public static bool TryDecodeState(P.SignState message, List<SignRecordData> target,
            out int revision, out bool full, out string reason)
        {
            revision = 0;
            full = false;
            reason = null;
            if (message == null)
            {
                reason = "state-null";
                return false;
            }
            if (message.Signs.Count > MaxSigns)
            {
                reason = "state-too-large:" + message.Signs.Count;
                return false;
            }
            target?.Clear();
            for (int i = 0; i < message.Signs.Count; i++)
            {
                if (!TryFromProto(message.Signs[i], out SignRecordData record, out string recordError))
                {
                    reason = "record:" + recordError;
                    return false;
                }
                target?.Add(record);
            }
            revision = message.Revision;
            full = message.Full;
            return true;
        }

        public static long Hash(IReadOnlyList<SignRecordData> signs)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                if (signs != null)
                    for (int i = 0; i < signs.Count; i++)
                    {
                        SignRecordData sign = signs[i];
                        if (sign == null) continue;
                        hash = Mix(hash, sign.X);
                        hash = Mix(hash, sign.Y);
                        hash = Mix(hash, sign.Z);
                        for (int line = 0; line < SignRecordData.LineCount; line++)
                        {
                            string text = sign.Lines[line] ?? string.Empty;
                            hash = Mix(hash, text.Length);
                            for (int c = 0; c < text.Length; c++) hash = Mix(hash, text[c]);
                            hash = Mix(hash, (int)sign.Colors[line]);
                        }
                        string url = sign.Url ?? string.Empty;
                        hash = Mix(hash, url.Length);
                        for (int c = 0; c < url.Length; c++) hash = Mix(hash, url[c]);
                    }
                return hash;
            }
        }

        private static long Mix(long hash, int value)
        {
            unchecked
            {
                hash ^= (uint)value;
                hash *= 1099511628211L;
                return hash;
            }
        }
    }
}

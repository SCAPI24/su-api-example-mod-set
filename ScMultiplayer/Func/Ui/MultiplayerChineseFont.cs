using Engine;
using Engine.Content;
using Engine.Graphics;
using Engine.Media;
using Game;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ScMultiplayer
{
    internal static class MultiplayerChineseFont
    {
        private const float Pericles18Scale = 0.632f * 34f / 43f;

        private const float Pericles32Scale = 0.632f * 59f / 43f;

        public static BitmapFont Font { get; private set; }

        public static BitmapFont TextInputFont { get; private set; }

        public static void Load()
        {
            Texture2D texture = ContentCache.Get<Texture2D>(
                "Mod/Fonts/chinese32", false);
            string data = ContentCache.Get<string>(
                "Mod/Fonts/chinese32data", false);
            if (texture == null || string.IsNullOrEmpty(data))
            {
                Log.Error("[ScMP] Chinese chat font resources were not found.");
                return;
            }
            BitmapFont raw = Parse(texture, data);
            Font = raw?.Clone(Pericles18Scale, Vector2.Zero);
            TextInputFont = raw?.Clone(Pericles32Scale, Vector2.Zero);
        }

        // Source: Mod/StringInterceptor/Plug/ChineseFontLoader.cs:ChineseFontLoader.ParseFont
        private static BitmapFont Parse(Texture2D texture, string data)
        {
            using var reader = new StringReader(data);
            char[] splitters = { ' ', '\t' };
            int glyphCount = int.Parse(reader.ReadLine() ?? "0",
                CultureInfo.InvariantCulture);
            var glyphs = new List<BitmapFont.Glyph>(glyphCount);
            for (int i = 0; i < glyphCount; i++)
            {
                string line = reader.ReadLine();
                if (line == null) break;
                string[] values = line.Split(splitters,
                    StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 7)
                {
                    var expanded = new string[8];
                    expanded[0] = " ";
                    Array.Copy(values, 0, expanded, 1, 7);
                    values = expanded;
                }
                if (values.Length < 8) continue;
                glyphs.Add(new BitmapFont.Glyph(
                    values[0][0],
                    new Vector2(ParseFloat(values[1]), ParseFloat(values[2])),
                    new Vector2(ParseFloat(values[3]), ParseFloat(values[4])),
                    new Vector2(ParseFloat(values[5]), ParseFloat(values[6])),
                    ParseFloat(values[7])));
            }
            if (glyphs.Count == 0) return null;
            float glyphHeight = ParseFloat(reader.ReadLine());
            string[] spacingValues = (reader.ReadLine() ?? string.Empty).Split(
                splitters, StringSplitOptions.RemoveEmptyEntries);
            Vector2 spacing = spacingValues.Length >= 2
                ? new Vector2(ParseFloat(spacingValues[0]), ParseFloat(spacingValues[1]))
                : Vector2.Zero;
            float scale = ParseFloat(reader.ReadLine());
            string fallbackLine = reader.ReadLine();
            char fallback = string.IsNullOrEmpty(fallbackLine) ? '?' : fallbackLine[0];
            var font = new BitmapFont(texture, glyphs, fallback,
                glyphHeight, spacing, scale);
            if (int.TryParse(reader.ReadLine(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int kerningCount))
            {
                for (int i = 0; i < kerningCount; i++)
                {
                    string[] values = (reader.ReadLine() ?? string.Empty).Split(
                        splitters, StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length >= 3)
                        font.SetKerning(values[0][0], values[1][0],
                            ParseFloat(values[2]));
                }
            }
            return font;
        }

        private static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out float result) ? result : 0f;
    }
}

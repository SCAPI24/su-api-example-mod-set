using System;
using System.Collections.Generic;
using System.IO;
using Engine;
using Engine.Content;
using Engine.Graphics;
using Engine.Media;

namespace StringInterceptor
{
    /// <summary>
    /// 加载中文字体——4 套独立尺寸字体（chinese12/18/24/32），各自有独立纹理和 glyph 数据。
    /// 加载后通过 Clone 调整 Scale，使渲染高度与对应 Pericles 字体一致。
    ///
    /// Pericles 渲染高度 = GlyphHeight × Scale(0.632)
    ///   Pericles12: 24×0.632 = 15.17
    ///   Pericles18: 34×0.632 = 21.49
    ///   Pericles24: 45×0.632 = 28.44
    ///   Pericles32: 59×0.632 = 37.29
    ///
    /// 中文字体 Scale 校准 = Pericles渲染高度 / 中文GlyphHeight
    ///   ChineseFont12: 15.17/16 = 0.948
    ///   ChineseFont18: 21.49/24 = 0.895
    ///   ChineseFont24: 28.44/32 = 0.889
    ///   ChineseFont32: 37.29/43 = 0.867
    /// </summary>
    public static class ChineseFontLoader
    {
        public static BitmapFont ChineseFont12 { get; private set; }
        public static BitmapFont ChineseFont18 { get; private set; }
        public static BitmapFont ChineseFont24 { get; private set; }
        public static BitmapFont ChineseFont32 { get; private set; }

        // Pericles Scale = 0.632, 中文字体 Scale 校准值
        private const float PericlesScale = 0.632f;
        private const float Scale12 = PericlesScale * 24f / 16f; // 0.948
        private const float Scale18 = PericlesScale * 34f / 24f; // 0.895
        private const float Scale24 = PericlesScale * 45f / 32f; // 0.889
        private const float Scale32 = PericlesScale * 59f / 43f; // 0.867

        /// <summary>
        /// 根据当前字体的 GlyphHeight 选择匹配尺寸的中文字体
        /// // Source: Pericles12.lst glyphHeight=24, Pericles18.lst glyphHeight=34, Pericles24.lst glyphHeight=45, Pericles32.lst glyphHeight=59
        /// </summary>
        public static BitmapFont GetClosestChineseFont(float glyphHeight)
        {
            if (glyphHeight <= 24f) return ChineseFont12 ?? ChineseFont18 ?? ChineseFont24 ?? ChineseFont32;
            if (glyphHeight <= 34f) return ChineseFont18 ?? ChineseFont24 ?? ChineseFont32;
            if (glyphHeight <= 45f) return ChineseFont24 ?? ChineseFont32;
            return ChineseFont32 ?? ChineseFont24 ?? ChineseFont18 ?? ChineseFont12;
        }

        public static void Load()
        {
            var raw12 = LoadFont("chinese12", "chinese12data");
            var raw18 = LoadFont("chinese18", "chinese18data");
            var raw24 = LoadFont("chinese24", "chinese24data");
            var raw32 = LoadFont("chinese32", "chinese32data");

            // Clone 并校准 Scale，使渲染高度与 Pericles 一致
            ChineseFont12 = raw12?.Clone(Scale12, Vector2.Zero);
            ChineseFont18 = raw18?.Clone(Scale18, Vector2.Zero);
            ChineseFont24 = raw24?.Clone(Scale24, Vector2.Zero);
            ChineseFont32 = raw32?.Clone(Scale32, Vector2.Zero);

            int loaded = (ChineseFont12 != null ? 1 : 0) + (ChineseFont18 != null ? 1 : 0)
                       + (ChineseFont24 != null ? 1 : 0) + (ChineseFont32 != null ? 1 : 0);
            Log.Information($"[ChineseFontLoader] Loaded {loaded}/4 font sizes (scale-calibrated to Pericles).");
        }

        /// <summary>
        /// 从 ContentCache 加载指定尺寸的中文字体（纹理 + glyph 数据）
        /// </summary>
        private static BitmapFont LoadFont(string texKey, string dataKey)
        {
            string fullTexKey = $"Mod/Fonts/{texKey}";
            string fullDataKey = $"Mod/Fonts/{dataKey}";

            var texture = ContentCache.Get<Texture2D>(fullTexKey, false);
            if (texture == null)
            {
                Log.Error($"[ChineseFontLoader] Texture '{fullTexKey}' not found.");
                return null;
            }

            var lstData = ContentCache.Get<string>(fullDataKey, false);
            if (lstData == null)
            {
                Log.Error($"[ChineseFontLoader] Glyph data '{fullDataKey}' not found.");
                return null;
            }

            return ParseFont(texture, lstData, texKey);
        }

        /// <summary>
        /// 解析 .lst 格式 glyph 数据，构建 BitmapFont
        /// // Source: BitmapFont.cs Initialize()
        /// </summary>
        private static BitmapFont ParseFont(Texture2D texture, string lstData, string name)
        {
            StringReader reader = null;
            try
            {
                char[] splitters = new[] { ' ', '\t' };

                reader = new StringReader(lstData);
                string firstLine = reader.ReadLine();
                if (firstLine == null) throw new FormatException("Missing glyph count line.");
                int glyphCount = int.Parse(firstLine);

                var glyphs = new List<BitmapFont.Glyph>();
                for (int i = 0; i < glyphCount; i++)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;

                    string[] arr = line.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
                    if (arr.Length == 7)
                    {
                        var tmp = new string[8];
                        tmp[0] = " ";
                        Array.Copy(arr, 0, tmp, 1, 7);
                        arr = tmp;
                    }
                    if (arr.Length < 8) continue;

                    char code = arr[0][0];
                    float texL = float.Parse(arr[1]);
                    float texT = float.Parse(arr[2]);
                    float texR = float.Parse(arr[3]);
                    float texB = float.Parse(arr[4]);
                    float offsetX = float.Parse(arr[5]);
                    float offsetY = float.Parse(arr[6]);
                    float advance = float.Parse(arr[7]);

                    var tc1 = new Vector2(texL, texT);
                    var tc2 = new Vector2(texR, texB);
                    var offset = new Vector2(offsetX, offsetY);

                    glyphs.Add(new BitmapFont.Glyph(code, tc1, tc2, offset, advance));
                }

                if (glyphs.Count == 0)
                {
                    Log.Error($"[ChineseFontLoader] No glyphs parsed for {name}.");
                    return null;
                }

                float glyphHeight = float.Parse(reader.ReadLine());
                string[] spacingArr = reader.ReadLine().Split(splitters, StringSplitOptions.RemoveEmptyEntries);
                var spacing = new Vector2(float.Parse(spacingArr[0]), float.Parse(spacingArr[1]));
                float scale = float.Parse(reader.ReadLine());
                char fallbackCode = reader.ReadLine()[0];

                var font = new BitmapFont(texture, glyphs, fallbackCode, glyphHeight, spacing, scale);

                // Kerning pairs
                string kerningCountLine = reader.ReadLine();
                if (kerningCountLine != null)
                {
                    int kerningCount = int.Parse(kerningCountLine);
                    for (int k = 0; k < kerningCount; k++)
                    {
                        string kline = reader.ReadLine();
                        if (kline == null) break;
                        string[] karr = kline.Split(splitters, StringSplitOptions.RemoveEmptyEntries);
                        if (karr.Length >= 3)
                        {
                            char c1 = karr[0][0];
                            char c2 = karr[1][0];
                            float amount = float.Parse(karr[2]);
                            font.SetKerning(c1, c2, amount);
                        }
                    }
                }

                Log.Information($"[ChineseFontLoader] {name}: {glyphs.Count} glyphs, height={glyphHeight:F0}, scale={scale}, spacing={spacing}");
                return font;
            }
            catch (Exception ex)
            {
                Log.Error($"[ChineseFontLoader] Parse {name} failed: {ex.Message}");
                return null;
            }
            finally
            {
                if (reader != null) reader.Dispose();
            }
        }
    }
}

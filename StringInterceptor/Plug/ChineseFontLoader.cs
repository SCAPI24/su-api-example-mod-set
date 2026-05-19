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
    /// 加载中文字体——解析 .lst 格式的 glyph 数据，构建 BitmapFont。
    /// ContentCache key:
    ///   Content/Fonts/ChinesePericles.png     → Mod/Fonts/ChinesePericles (Texture2D)
    ///   Content/Fonts/ChinesePericlesData.txt → Mod/Fonts/ChinesePericlesData (string, .lst 格式)
    ///
    /// .lst 格式（来源 SurvivalcraftApi Pericles.lst）：
    ///   行1: glyph 总数
    ///   行2-N: <char> <texL> <texT> <texR> <texB> <offsetX> <offsetY> <advance>
    ///   (tex 坐标已归一化 0-1，offset/advance 为像素)
    ///   N+1: glyphHeight
    ///   N+2: spacing.x spacing.y  
    ///   N+3: scale
    ///   N+4: fallbackCode
    ///   N+5: kerning 对数
    ///   N+6+: <char> <char> <amount> (kerning pairs)
    /// </summary>
    public static class ChineseFontLoader
    {
        public static BitmapFont ChineseFont { get; private set; }

        // 4 种尺寸 Clone，全部使用 Pericles Scale=0.632
        public static BitmapFont ChineseFont12 { get; private set; }
        public static BitmapFont ChineseFont18 { get; private set; }
        public static BitmapFont ChineseFont24 { get; private set; }
        public static BitmapFont ChineseFont32 { get; private set; }

        private static readonly float PericlesScale = 0.632f;

        /// <summary>
        /// 根据当前字体的 GlyphHeight 选择匹配尺寸的中文字体
        /// </summary>
        public static BitmapFont GetClosestChineseFont(float glyphHeight)
        {
            if (glyphHeight == 24f) return ChineseFont12;
            if (glyphHeight == 34f) return ChineseFont18;
            if (glyphHeight == 45f) return ChineseFont24;
            if (glyphHeight == 59f) return ChineseFont32;
            // 默认
            float diff = Math.Abs(glyphHeight - 24f);
            var best = ChineseFont12;
            float d = Math.Abs(glyphHeight - 34f);
            if (d < diff) { diff = d; best = ChineseFont18; }
            d = Math.Abs(glyphHeight - 45f);
            if (d < diff) { diff = d; best = ChineseFont24; }
            d = Math.Abs(glyphHeight - 59f);
            if (d < diff) { best = ChineseFont32; }
            return best;
        }

        public static void Load()
        {
            var texture = ContentCache.Get<Texture2D>("Mod/Fonts/ChinesePericles", false);
            if (texture == null)
            {
                Log.Error("[ChineseFontLoader] Texture 'Mod/Fonts/ChinesePericles' not found.");
                return;
            }

            var lstData = ContentCache.Get<string>("Mod/Fonts/ChinesePericlesData", false);
            if (lstData == null)
            {
                Log.Error("[ChineseFontLoader] Glyph data 'Mod/Fonts/ChinesePericlesData' not found.");
                return;
            }

            StringReader reader = null;
            try
            {
                // 解析 .lst 格式 — Source: BitmapFont.cs Initialize()
                char[] splitters = new[] { ' ', '\t' };

                reader = new StringReader(lstData);
                string firstLine = reader.ReadLine();
                if (firstLine == null) throw new FormatException("Missing glyph count line.");
                int glyphCount = int.Parse(firstLine);

                // 解析 glyph 行
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
                    Log.Error("[ChineseFontLoader] No glyphs parsed.");
                    return;
                }

                float glyphHeight = float.Parse(reader.ReadLine());
                string[] spacingArr = reader.ReadLine().Split(splitters, StringSplitOptions.RemoveEmptyEntries);
                var spacing = new Vector2(float.Parse(spacingArr[0]), float.Parse(spacingArr[1]));
                float scale = float.Parse(reader.ReadLine());
                char fallbackCode = reader.ReadLine()[0];

                // 构建 BitmapFont — public constructor
                ChineseFont = new BitmapFont(texture, glyphs, fallbackCode, glyphHeight, spacing, scale);

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
                            ChineseFont.SetKerning(c1, c2, amount);
                        }
                    }
                }

                Log.Information($"[ChineseFontLoader] Loaded {glyphs.Count} glyphs, height={glyphHeight:F0}, scale={scale}, spacing={spacing}");

                // Clone 4 种尺寸，Scale 与 Pericles 一致 (0.632)
                ChineseFont12 = ChineseFont.Clone(PericlesScale, Vector2.Zero);
                ChineseFont18 = ChineseFont.Clone(PericlesScale, Vector2.Zero);
                ChineseFont24 = ChineseFont.Clone(PericlesScale, Vector2.Zero);
                ChineseFont32 = ChineseFont.Clone(PericlesScale, Vector2.Zero);

                Log.Information($"[ChineseFontLoader] Clones all Scale={PericlesScale}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ChineseFontLoader] Parse failed: {ex.Message}");
            }
            finally
            {
                if (reader != null) reader.Dispose();
            }
        }

    }
}

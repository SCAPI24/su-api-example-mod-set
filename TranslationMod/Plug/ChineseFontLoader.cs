using Engine.Media;
using SuAPICore;

namespace TranslationMod
{
    // Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetClosest
    public static class ChineseFontLoader
    {
        private static readonly System.Collections.Generic.HashSet<BitmapFont> SharedFonts =
            new System.Collections.Generic.HashSet<BitmapFont>();

        public static BitmapFont ChineseFont12 => Track(SuAPIFonts.GetPericles12());
        public static BitmapFont ChineseFont18 => Track(SuAPIFonts.GetPericles18());
        public static BitmapFont ChineseFont24 => Track(SuAPIFonts.GetPericles24());
        public static BitmapFont ChineseFont32 => Track(SuAPIFonts.GetPericles32());
        public static BitmapFont RawChineseFont32 => Track(SuAPIFonts.GetRawChinese32());

        public static BitmapFont GetClosestChineseFont(float glyphHeight) =>
            Track(SuAPIFonts.GetClosest(glyphHeight * 0.632f));

        // Keep this compatibility check in the Mod so it also works with an older built-in Core.
        // Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetPericles12
        public static bool IsChineseFont(BitmapFont font) =>
            font != null && SharedFonts.Contains(font);

        // Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetPericles12
        // Profiles are deliberately lazy, so loading the translation Mod does not upload
        // the shared atlas until a translated widget actually needs a font.
        public static void Load() { }

        private static BitmapFont Track(BitmapFont font)
        {
            if (font != null)
                SharedFonts.Add(font);
            return font;
        }
    }
}

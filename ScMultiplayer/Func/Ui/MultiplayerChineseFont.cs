using Engine.Media;
using SuAPICore;

namespace ScMultiplayer
{
    // Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetPericles18
    internal static class MultiplayerChineseFont
    {
        private static BitmapFont s_font;
        private static BitmapFont s_textInputFont;

        public static BitmapFont Font => s_font ??= SuAPIFonts.GetPericles18();

        public static BitmapFont TextInputFont =>
            s_textInputFont ??= SuAPIFonts.GetPericles32();

        // Source: EntitySystem/SuAPICore/SuAPIFonts.cs:SuAPIFonts.GetPericles18
        // Loading runs before the gameplay frame loop. Warm both profiles here so the
        // shared atlas and its font profiles are not created by a turn, TA, or IF action.
        public static void Load()
        {
            s_font ??= SuAPIFonts.GetPericles18();
            s_textInputFont ??= SuAPIFonts.GetPericles32();
        }
    }
}

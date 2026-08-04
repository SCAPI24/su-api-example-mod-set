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
        // Defer the single shared atlas until a multiplayer widget needs text.
        public static void Load()
        {
        }
    }
}

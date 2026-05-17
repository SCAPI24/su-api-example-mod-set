using Engine;

// Texture slot ↔ texture coordinate mapping utility
// Source: SCAPI24/SurvivalCraftMap Tool.cs

namespace SurvivalcraftMiniMap
{
    public class Tool
    {
        private static Vector4[] slotTexCoords = new Vector4[256];
        public static Vector4[] SlotTexCoords { get => slotTexCoords; set => slotTexCoords = value; }

        public static void CalculateSlotTexCoordTables()
        {
            for (int i = 0; i < 256; i++)
            {
                SlotTexCoords[i] = TextureSlotToTextureCoords(i);
            }
        }

        public static Vector4 TextureSlotToTextureCoords(int slot)
        {
            int num = slot % 16;
            int num2 = slot / 16;
            float x = ((float)num + 0.001f) / 16f;
            float y = ((float)num2 + 0.001f) / 16f;
            float z = ((float)(num + 1) - 0.001f) / 16f;
            float w = ((float)(num2 + 1) - 0.001f) / 16f;
            return new Vector4(x, y, z, w);
        }
    }
}

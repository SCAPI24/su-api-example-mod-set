using Engine;
using Engine.Media;
using System;
using System.IO;

namespace ScMultiplayer
{
    // Source: ScMultiplayerProfileHandlers.ValidateSkinAssetData
    // Image dimension validation only; skin class validation remains with the profile handler.
    internal static class SkinImageValidator
    {
        public static void Validate(byte[] data, int maximumBytes)
        {
            if (data == null || data.Length == 0 || data.Length > maximumBytes)
                throw new InvalidOperationException("Invalid character skin size.");
            Image image = Image.Load(new MemoryStream(data));
            if (image.Width > 256 || image.Height > 256)
                throw new InvalidOperationException(
                    $"Character skin is larger than 256x256 pixels (size={image.Width}x{image.Height}).");
            if (!MathUtils.IsPowerOf2(image.Width) || !MathUtils.IsPowerOf2(image.Height))
                throw new InvalidOperationException(
                    $"Character skin does not have power-of-two size (size={image.Width}x{image.Height}).");
        }
    }
}

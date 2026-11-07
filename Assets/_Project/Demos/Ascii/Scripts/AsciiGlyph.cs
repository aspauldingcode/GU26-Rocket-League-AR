using UnityEngine;

namespace CardboardXR.Demos.Ascii
{
    // Pure helpers for the ASCII surface look: luminance and which atlas glyph a brightness picks.
    // The shader mirrors BrightnessToIndex so EditMode tests and the GPU stay aligned.
    public static class AsciiGlyph
    {
        // Classic sparse-to-dense ramp used for the atlas and the portal lintel labels.
        public const string Ramp = @" `.-':,_""^~;!><*\/+?rcL|)(vJizl}{Yt[]xC1IfnuojakhZUXwbdqpOm#8%0WBM$Q&@";

        public static float Luminance(Color color) =>
            color.r * 0.299f + color.g * 0.587f + color.b * 0.114f;

        // Index in [0, glyphCount): darker → lower glyphs, brighter → denser ones.
        public static int BrightnessToIndex(float luminance01, int glyphCount)
        {
            if (glyphCount <= 1)
                return 0;
            var t = Mathf.Clamp01(luminance01);
            return Mathf.Min(glyphCount - 1, Mathf.FloorToInt(t * (glyphCount - 1) + 1e-4f));
        }

        public static char BrightnessToChar(float luminance01) =>
            Ramp[BrightnessToIndex(luminance01, Ramp.Length)];
    }
}

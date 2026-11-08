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

        // Phosphor brightness from eye depth in meters. The terminal shader uses this same curve.
        // Arm's length stays bright; a few meters back is clearly dimmer; the far wall stays visible.
        public static float DepthBrightness(float eyeDepthMeters)
        {
            var d = Mathf.Max(0f, eyeDepthMeters);
            var falloff = 1f / (1f + d * d * 0.55f);
            return Mathf.Lerp(0.08f, 1f, falloff);
        }

        // Screen-pixel scale of one character. Nested powers of two so a near tile lines up with
        // the far grid. The terminal shader uses the same steps. 4 is arm's length, 1 is across the room.
        public static int CellScale(float eyeDepthMeters)
        {
            var d = Mathf.Max(0f, eyeDepthMeters);
            if (d < 1.05f)
                return 4;
            if (d < 2.6f)
                return 2;
            return 1;
        }

        public static Vector2 CellSize(float eyeDepthMeters)
        {
            var scale = CellScale(eyeDepthMeters);
            return new Vector2(8f * scale, 12f * scale);
        }

        // Depth picks the glyph band. Surface lighting only moves inside that band, so a dark
        // nearby surface stays denser than a lit one across the room.
        public static float GlyphPick(float form01, float depthBrightness)
        {
            var form = Mathf.Clamp01(form01);
            var depth = Mathf.Clamp01(depthBrightness);
            var low = Mathf.Lerp(0f, 0.72f, depth);
            var high = Mathf.Lerp(0.12f, 1f, depth);
            return Mathf.Lerp(low, high, form);
        }

        // A cast or self shadow drops the character toward the sparse end of the ramp.
        public static float ShadowedPick(float pick, float shadow01) =>
            Mathf.Lerp(pick, pick * 0.2f, Mathf.Clamp01(shadow01));
    }
}

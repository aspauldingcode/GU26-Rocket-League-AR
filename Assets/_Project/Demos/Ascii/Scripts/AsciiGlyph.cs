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
        // A smooth fall across the whole room: close stays bright, and the far edge is black.
        public static float DepthBrightness(float eyeDepthMeters)
        {
            var d = Mathf.Max(0f, eyeDepthMeters);
            return 1f - SmoothStep(0.4f, 6.5f, d);
        }

        // HLSL smoothstep(edge0, edge1, x). Mathf.SmoothStep takes (from, to, t) and is a different curve.
        static float SmoothStep(float edge0, float edge1, float x)
        {
            var t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        // Screen-pixel scale of one character. The room uses the small cell so the grid holds many
        // symbols. Arm's length is twice that, and the two grids share edges. The shader matches.
        public static int CellScale(float eyeDepthMeters)
        {
            var d = Mathf.Max(0f, eyeDepthMeters);
            return d < 1.05f ? 2 : 1;
        }

        public static Vector2 CellSize(float eyeDepthMeters)
        {
            var scale = CellScale(eyeDepthMeters);
            return new Vector2(6f * scale, 10f * scale);
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

#ifndef CARDBOARDXR_ASCII_GLYPHS_INCLUDED
#define CARDBOARDXR_ASCII_GLYPHS_INCLUDED

// Glyph math shared by the terminal pass and the angels. Mirrors AsciiGlyph.cs.

// The atlas is a row of 48x48 cells. Every glyph's ink sits inside this box (texels from the
// bottom-left, one texel of padding). Sampling the whole cell squeezed thin font strokes 8:1 into
// a 6x10 tile, which broke them into per-pixel speckle.
static const float2 k_AsciiGlyphBoxMin = float2(13.0, 3.0) / 48.0;
static const float2 k_AsciiGlyphBoxSize = float2(23.0, 40.0) / 48.0;
static const float2 k_AsciiFarCell = float2(6.0, 10.0);
static const float2 k_AsciiNearCell = float2(12.0, 20.0);
static const float k_AsciiNearDepth = 1.05;

float AsciiDepthBrightness(float eyeDepth)
{
    float d = max(eyeDepth, 0.0);
    return 1.0 - smoothstep(0.4, 6.5, d);
}

float2 AsciiCellPixels(float eyeDepth)
{
    return eyeDepth < k_AsciiNearDepth ? k_AsciiNearCell : k_AsciiFarCell;
}

// Depth chooses the band; form only slides inside it.
float AsciiGlyphPick(float form, float depthBrightness)
{
    float depth = saturate(depthBrightness);
    float low = lerp(0.0, 0.72, depth);
    float high = lerp(0.12, 1.0, depth);
    return lerp(low, high, saturate(form));
}

// Ink coverage of the glyph for `pick` at `inCell`. Gradients come from the tile size rather than
// ddx/ddy, so the mip level is the same across a tile and never jumps at its border.
half AsciiGlyphInk(TEXTURE2D_PARAM(atlas, atlasSampler), float count, float pick, float2 inCell, float2 cellPixels)
{
    count = max(count, 1.0);
    float indexF = saturate(pick) * (count - 1.0);
    float index0 = floor(indexF);
    float index1 = min(index0 + 1.0, count - 1.0);
    // A tile shows one letter; only the middle of the step eases, so two letters never ghost.
    float blend = smoothstep(0.35, 0.65, indexF - index0);
    float2 box = k_AsciiGlyphBoxMin + saturate(inCell) * k_AsciiGlyphBoxSize;
    float2 gradX = float2(k_AsciiGlyphBoxSize.x / (cellPixels.x * count), 0.0);
    float2 gradY = float2(0.0, k_AsciiGlyphBoxSize.y / cellPixels.y);
    half a = SAMPLE_TEXTURE2D_GRAD(atlas, atlasSampler, float2((index0 + box.x) / count, box.y), gradX, gradY).r;
    half b = SAMPLE_TEXTURE2D_GRAD(atlas, atlasSampler, float2((index1 + box.x) / count, box.y), gradX, gradY).r;
    return smoothstep(0.06, 0.55, lerp(a, b, blend));
}

#endif

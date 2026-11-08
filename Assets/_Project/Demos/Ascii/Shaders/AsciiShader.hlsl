#ifndef CARDBOARDXR_ASCII_SHADER_INCLUDED
#define CARDBOARDXR_ASCII_SHADER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "AsciiGlyphs.hlsl"

// Room pixels are a green mask. The terminal pass turns that mask into screen-space glyphs.
// A figure (a weeping angel) draws its own glyphs here. The eye target clamps colors above 1,
// so a flat HDR mask never survived long enough for the terminal to see it.
// A window (the doorway seen from inside) writes magenta, and the terminal puts the camera there.
CBUFFER_START(UnityPerMaterial)
    half4 _RoomColor;
    half4 _FurnitureColor;
    float _ForceFurniture;
    float _UseMeshNormal;
    float _Volume;
    float _Figure;
    float _Window;
    float _Extrude;
    float4 _GlyphAtlas_ST;
    float _GlyphCount;
CBUFFER_END

TEXTURE2D(_GlyphAtlas);
SAMPLER(sampler_GlyphAtlas);

struct AsciiAttributes
{
    float3 positionOS : POSITION;
    float3 normalOS : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct AsciiVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};

AsciiVaryings AsciiVert(AsciiAttributes input)
{
    AsciiVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 meshN = TransformObjectToWorldNormal(input.normalOS);
    float3 planeN = TransformObjectToWorldDir(float3(0.0, 1.0, 0.0));
    float3 normalWS = (_UseMeshNormal > 0.5 && dot(meshN, meshN) > 0.5) ? normalize(meshN) : planeN;
    float3 positionWS = TransformObjectToWorld(input.positionOS) + normalWS * _Extrude;
    output.positionCS = TransformWorldToHClip(positionWS);
    output.positionWS = positionWS;
    output.normalWS = normalWS;
    return output;
}

// Stone under a key light from above, a dim fill from behind, sky from overhead and a rim at the
// silhouette. Creases (fast-turning normals) go dark, so folds and wings read as carved.
half3 AsciiAngel(AsciiVaryings input, float3 n, float3 lightDir)
{
    float3 toEye = GetWorldSpaceNormalizeViewDir(input.positionWS);
    float key = saturate(dot(n, lightDir));
    float fill = saturate(dot(n, normalize(float3(-0.6, 0.2, -0.5)))) * 0.3;
    float sky = (0.5 + 0.5 * n.y) * 0.22;
    float rim = pow(1.0 - saturate(dot(n, toEye)), 3.0) * 0.35;
    float crease = saturate(length(fwidth(n)) * 3.0);
    float lit = smoothstep(0.0, 1.0, saturate(key * 0.8 + fill + sky + rim)) * lerp(1.0, 0.45, crease);

    // Screen pixels, not the mesh UVs, so the characters stand upright on the display.
    float depth = max(-TransformWorldToView(input.positionWS).z, 0.05);
    float2 size = max(GetScaledScreenParams().xy, float2(1.0, 1.0));
    float2 pixel = GetNormalizedScreenSpaceUV(input.positionCS.xy) * size;
    float2 cell = AsciiCellPixels(depth);
    float2 cellCoord = floor(pixel / cell);
    float2 inCell = pixel / cell - cellCoord;

    float depthBright = AsciiDepthBrightness(depth);
    float pick = AsciiGlyphPick(pow(lit, 0.8), depthBright);
    half glyph = AsciiGlyphInk(TEXTURE2D_ARGS(_GlyphAtlas, sampler_GlyphAtlas), _GlyphCount, pick, inCell, cell);
    // The same letter, slightly enlarged, as a faint glow around the strokes.
    half halo = AsciiGlyphInk(TEXTURE2D_ARGS(_GlyphAtlas, sampler_GlyphAtlas), _GlyphCount, pick,
        0.5 + (inCell - 0.5) * 0.78, cell);

    half3 stone = lerp(half3(0.30, 0.31, 0.34), half3(0.66, 0.64, 0.59), lit);
    half3 ink = stone * lerp(0.55, 1.0, lit) * depthBright;
    half3 paper = half3(0.015, 0.015, 0.018) * depthBright;
    half3 color = lerp(paper, ink, glyph) + stone * halo * 0.16 * depthBright;
    // Within reach the statue fills in as solid stone, with the letters carved into it.
    half solid = (1.0 - smoothstep(0.7, 2.0, depth)) * 0.7;
    half3 carved = stone * lerp(0.62, 1.0, glyph) * depthBright;
    return lerp(color, carved, solid);
}

half4 AsciiFrag(AsciiVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    if (_Window > 0.5)
        return half4(1.0, 0.0, 1.0, 1.0);

    float3 n = normalize(input.normalWS);
    // Same direction the terminal marches when it casts a shadow.
    float3 lightDir = normalize(float3(0.25, 0.9, 0.3));
    if (_Figure > 0.5)
        return half4(AsciiAngel(input, n, lightDir), 1.0);

    // Wide terminator so a face eases from lit to shadow instead of snapping.
    float lit = smoothstep(0.0, 0.9, saturate(dot(n, lightDir)));
    // 0.45 is the green floor the terminal's form readout expects.
    return half4(_RoomColor.rgb * lerp(0.45, 1.0, lit), 1.0);
}

#endif

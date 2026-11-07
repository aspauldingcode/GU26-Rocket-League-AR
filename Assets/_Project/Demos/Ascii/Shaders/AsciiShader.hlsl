#ifndef CARDBOARDXR_ASCII_SHADER_INCLUDED
#define CARDBOARDXR_ASCII_SHADER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_GlyphAtlas);
SAMPLER(sampler_GlyphAtlas);

CBUFFER_START(UnityPerMaterial)
    float4 _GlyphAtlas_ST;
    float _GlyphCount;
    float _CellSize;
    half4 _Color;
    half4 _BgColor;
    float _Extrude;
    float _ColorBoost;
    float _BgFromCamera;
CBUFFER_END

// Globals from AsciiSurfaces: the passthrough camera image, the AR camera's world-to-local matrix
// and the image's frustum at unit distance (center xy, size zw), so each cell takes the color the
// camera saw at that point of the room. Outside the gate, _AsciiClip keeps only the far side of
// the gate's plane (_AsciiClipPlane: normal xyz, offset w).
TEXTURE2D(_AsciiCameraTex);
SAMPLER(sampler_AsciiCameraTex);
float4x4 _AsciiWorldToCamera;
float4 _AsciiFrustum;
float _AsciiUseCamera;
float4 _AsciiClipPlane;
float _AsciiClip;

struct AsciiAttributes
{
    float3 positionOS : POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct AsciiVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};

// AR plane meshes carry no normals; the plane's own up (local +y) is its normal.
AsciiVaryings AsciiVert(AsciiAttributes input)
{
    AsciiVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    float3 normalWS = TransformObjectToWorldDir(float3(0.0, 1.0, 0.0));
    float3 positionWS = TransformObjectToWorld(input.positionOS) + normalWS * _Extrude;
    output.positionCS = TransformWorldToHClip(positionWS);
    output.positionWS = positionWS;
    output.normalWS = normalWS;
    return output;
}

float AsciiHash21(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// A basis in the surface: on walls, v is world up and u runs right as seen from the room; on
// floors and ceilings, u and v follow world x and z, so the grid never slides as a plane grows.
void AsciiSurfaceBasis(float3 n, out float3 u, out float3 v)
{
    if (abs(n.y) > 0.7)
    {
        u = float3(1.0, 0.0, 0.0);
        v = float3(0.0, 0.0, n.y > 0.0 ? 1.0 : -1.0);
    }
    else
    {
        v = normalize(float3(0.0, 1.0, 0.0) - n * n.y);
        u = normalize(cross(n, v));
    }
}

bool AsciiCameraColor(float3 positionWS, out half3 color)
{
    color = 0;
    if (_AsciiUseCamera < 0.5)
        return false;
    float4 camera = mul(_AsciiWorldToCamera, float4(positionWS, 1.0));
    if (camera.z < 0.05)
        return false;
    float2 uv = (camera.xy / camera.z - _AsciiFrustum.xy) / _AsciiFrustum.zw + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0))
        return false;
    color = SAMPLE_TEXTURE2D_LOD(_AsciiCameraTex, sampler_AsciiCameraTex, uv, 0).rgb;
    return true;
}

half4 AsciiFrag(AsciiVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    if (_AsciiClip > 0.5 && dot(_AsciiClipPlane.xyz, input.positionWS) + _AsciiClipPlane.w < 0.0)
        discard;

    float3 n = normalize(input.normalWS);
    float3 u, v;
    AsciiSurfaceBasis(n, u, v);

    float cell = max(_CellSize, 1e-3);
    float2 grid = float2(dot(input.positionWS, u), dot(input.positionWS, v)) / cell;
    float2 cellCoord = floor(grid);
    float2 inCell = grid - cellCoord;

    // One color per cell, read where the cell's center is in the camera image.
    float2 toCenter = (cellCoord + 0.5 - grid) * cell;
    float3 centerWS = input.positionWS + u * toCenter.x + v * toCenter.y;

    half3 camera;
    half3 ink;
    half3 paper;
    float lum;
    if (AsciiCameraColor(centerWS, camera))
    {
        // The project is linear; glyph density follows perceived brightness.
        lum = pow(saturate(dot(camera, half3(0.2126, 0.7152, 0.0722))), 1.0 / 2.2);
        ink = saturate(camera * _ColorBoost);
        paper = _BgColor.rgb + camera * _BgFromCamera;
    }
    else
    {
        half ndotl = saturate(dot(n, normalize(half3(0.25, 0.9, 0.35))) * 0.5 + 0.5);
        lum = saturate(ndotl * 0.7 + AsciiHash21(cellCoord) * 0.3);
        ink = _Color.rgb;
        paper = _BgColor.rgb;
    }

    // Matches AsciiGlyph.BrightnessToIndex: denser glyphs for brighter cells. Gradients come from
    // the continuous grid so the cell edges don't pick the smallest mip.
    float count = max(_GlyphCount, 1.0);
    float index = floor(saturate(lum) * (count - 1.0) + 1e-4);
    float2 atlasScale = float2(1.0 / count, 1.0);
    float2 atlasUV = float2((index + inCell.x) / count, inCell.y);
    half glyph = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV,
        ddx(grid) * atlasScale, ddy(grid) * atlasScale).r;

    return half4(lerp(paper, ink, glyph), 1.0);
}

#endif

#ifndef CARDBOARDXR_ASCII_SHADER_INCLUDED
#define CARDBOARDXR_ASCII_SHADER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Shading only. Glyphs are a screen-space pass (AsciiTerminal.shader), so characters stay
// upright in front of the camera instead of lying on the surface. Raised tops (tables, seats)
// are yellow; floor and walls stay green. Stencil 1 marks pixels the terminal pass may replace.
CBUFFER_START(UnityPerMaterial)
    half4 _RoomColor;
    half4 _FurnitureColor;
    float _ForceFurniture;
    float _Extrude;
CBUFFER_END

float _AsciiFloorY;

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

half4 AsciiFrag(AsciiVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float3 n = normalize(input.normalWS);
    float3 lightDir = normalize(float3(0.25, 0.9, 0.3));
    float ndotl = saturate(dot(n, lightDir) * 0.65 + 0.35);
    float dist = distance(input.positionWS, _WorldSpaceCameraPos);
    float near = saturate(1.0 - dist / 7.0);
    float shade = lerp(0.4, 1.0, ndotl) * lerp(0.55, 1.0, near);

    // Up-facing surfaces above the floor are what you trip on. Walls and the floor stay room green.
    float raised = n.y > 0.6 && input.positionWS.y > _AsciiFloorY + 0.15;
    float furniture = _ForceFurniture > 0.5 || raised;
    half3 albedo = furniture > 0.5 ? _FurnitureColor.rgb : _RoomColor.rgb;
    return half4(albedo * shade, furniture > 0.5 ? 0.75 : 0.45);
}

#endif

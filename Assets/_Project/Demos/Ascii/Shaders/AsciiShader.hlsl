#ifndef CARDBOARDXR_ASCII_SHADER_INCLUDED
#define CARDBOARDXR_ASCII_SHADER_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// Mask and lighting only. Glyphs are a screen-space pass (AsciiTerminal.shader): characters stay
// upright on the screen, and that pass chooses their size from depth. The room is one green.
// A figure (a weeping angel) is HDR white, above 1, so a white hand is not mistaken for it.
CBUFFER_START(UnityPerMaterial)
    half4 _RoomColor;
    half4 _FurnitureColor;
    float _ForceFurniture;
    float _UseMeshNormal;
    float _Volume;
    float _Figure;
    float _Extrude;
CBUFFER_END

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

half4 AsciiFrag(AsciiVaryings input) : SV_Target
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float3 n = normalize(input.normalWS);
    // Same direction the terminal marches when it casts a shadow.
    float3 lightDir = normalize(float3(0.25, 0.9, 0.3));
    float ndotl = saturate(dot(n, lightDir));
    // Sharp terminator: the dark side of a mesh reads as a shadow, not a mild tint.
    float lit = smoothstep(0.12, 0.5, ndotl);
    if (_Figure > 0.5)
    {
        float signal = lerp(1.5, 4.0, lit);
        return half4(signal, signal, signal, 1.0);
    }
    // 0.45 is the green floor the terminal's form readout expects.
    float shade = lerp(0.45, 1.0, lit);
    return half4(_RoomColor.rgb * shade, 1.0);
}

#endif

Shader "CardboardXR/Demos/AsciiShader"
{
    Properties
    {
        _GlyphAtlas ("Glyph Atlas", 2D) = "white" {}
        _GlyphCount ("Glyph Count", Float) = 70
        _CellSize ("Cell Size (m)", Float) = 0.028
        _Color ("Phosphor", Color) = (0.25, 1, 0.45, 1)
        _BgColor ("Background", Color) = (0.02, 0.05, 0.03, 0.92)
        _Extrude ("Vertex Extrude (m)", Float) = 0.004
        _AsciiCameraTex ("Camera (optional)", 2D) = "gray" {}
        _UseCamera ("Use Camera", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "AsciiShader"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_GlyphAtlas);
            SAMPLER(sampler_GlyphAtlas);
            TEXTURE2D(_AsciiCameraTex);
            SAMPLER(sampler_AsciiCameraTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _GlyphAtlas_ST;
                float _GlyphCount;
                float _CellSize;
                half4 _Color;
                half4 _BgColor;
                float _Extrude;
                float _UseCamera;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS);
                positionWS += normalWS * _Extrude;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.screenPos = ComputeScreenPos(output.positionCS);
                return output;
            }

            // Mobile-safe hash (the sin-based one loses precision at large coordinates)
            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            // Matches AsciiGlyph.BrightnessToIndex: denser glyphs for brighter cells.
            float2 AtlasUV(float lum, float2 inCell, float count)
            {
                float n = max(count, 1.0);
                float idx = floor(saturate(lum) * (n - 1.0) + 1e-4);
                float u0 = idx / n;
                float u1 = (idx + 1.0) / n;
                return float2(lerp(u0, u1, inCell.x), inCell.y);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float cell = max(_CellSize, 1e-4);
                float3 n = abs(normalize(input.normalWS));
                float2 planar;
                if (n.y >= n.x && n.y >= n.z)
                    planar = input.positionWS.xz;
                else if (n.x >= n.z)
                    planar = input.positionWS.zy;
                else
                    planar = input.positionWS.xy;

                float2 cellCoord = floor(planar / cell);
                float2 inCell = frac(planar / cell);

                float lum;
                if (_UseCamera > 0.5)
                {
                    float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                    half3 cam = SAMPLE_TEXTURE2D(_AsciiCameraTex, sampler_AsciiCameraTex, screenUV).rgb;
                    lum = dot(cam, half3(0.299, 0.587, 0.114));
                }
                else
                {
                    half ndotl = saturate(dot(normalize(input.normalWS), normalize(half3(0.25, 0.9, 0.35))));
                    float hash = Hash21(cellCoord);
                    lum = saturate(ndotl * 0.85 + hash * 0.15);
                }

                half glyph = SAMPLE_TEXTURE2D(_GlyphAtlas, sampler_GlyphAtlas, AtlasUV(lum, inCell, _GlyphCount)).r;
                half3 phosphor = _Color.rgb * glyph;
                half3 rgb = lerp(_BgColor.rgb, phosphor, glyph);
                half alpha = lerp(_BgColor.a, _Color.a, glyph);
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
}
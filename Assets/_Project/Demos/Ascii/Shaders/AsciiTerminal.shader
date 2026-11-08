Shader "CardboardXR/Demos/AsciiTerminal"
{
    Properties
    {
        _GlyphAtlas ("Glyph Atlas", 2D) = "white" {}
        _GlyphCount ("Glyph Count", Float) = 70
        _Cell ("Cell Size (px)", Vector) = (12, 20, 0, 0)
        _Ink ("Phosphor", Color) = (0.25, 1, 0.45, 1)
        _Paper ("Background", Color) = (0.01, 0.04, 0.02, 1)
        _Warn ("Furniture Ink", Color) = (1, 0.86, 0.15, 1)
        _WarnPaper ("Furniture Paper", Color) = (0.12, 0.07, 0.01, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "AsciiTerminal"
            ZTest Always
            ZWrite Off
            Cull Off
            Stencil
            {
                Ref 1
                Comp Equal
                Pass Keep
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_GlyphAtlas);
            SAMPLER(sampler_GlyphAtlas);

            CBUFFER_START(UnityPerMaterial)
                float4 _GlyphAtlas_ST;
                float _GlyphCount;
                float4 _Cell;
                half4 _Ink;
                half4 _Paper;
                half4 _Warn;
                half4 _WarnPaper;
            CBUFFER_END

            bool AsciiGreen(half3 color)
            {
                return color.g > 0.03 && color.g > color.r * 1.2 && color.g > color.b * 1.15;
            }

            bool AsciiYellow(half3 color)
            {
                return color.r > 0.04 && color.g > 0.04 && color.b < color.g * 0.55
                    && color.r > color.b * 1.35 && abs(color.r - color.g) < max(color.r, color.g) * 0.45;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float2 size = max(_BlitTexture_TexelSize.zw, float2(1.0, 1.0));
                float2 cell = max(_Cell.xy, float2(4.0, 6.0));
                float2 pixel = uv * size;
                float2 cellCoord = floor(pixel / cell);
                float2 inCell = pixel / cell - cellCoord;
                float2 centerUv = ((cellCoord + 0.5) * cell) / size;

                half4 src = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, centerUv, 0);
                bool yellow = AsciiYellow(src.rgb);
                bool tagged = yellow || AsciiGreen(src.rgb);
                if (!tagged)
                    return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);

                float lum = dot(src.rgb, half3(0.2126, 0.7152, 0.0722));
                float2 cellUv = cell / size;
                float depth = LinearEyeDepth(SampleSceneDepth(centerUv), _ZBufferParams);
                if (depth < 40.0)
                {
                    float across = LinearEyeDepth(SampleSceneDepth(centerUv + float2(cellUv.x, 0.0)), _ZBufferParams);
                    float down = LinearEyeDepth(SampleSceneDepth(centerUv + float2(0.0, cellUv.y)), _ZBufferParams);
                    float edge = saturate((max(abs(depth - across), abs(depth - down)) - 0.03) * 8.0);
                    lum = saturate(max(lum, edge));
                }

                float count = max(_GlyphCount, 1.0);
                float index = floor(saturate(lum) * (count - 1.0) + 1e-4);
                float2 atlasScale = float2(1.0 / count, 1.0);
                float2 atlasUV = float2((index + inCell.x) / count, inCell.y);
                float2 grid = pixel / cell;
                half glyph = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV,
                    ddx(grid) * atlasScale, ddy(grid) * atlasScale).r;

                half3 paper = yellow ? _WarnPaper.rgb : _Paper.rgb;
                half3 ink = yellow ? _Warn.rgb : _Ink.rgb;
                return half4(lerp(paper, ink, glyph), 1.0);
            }
            ENDHLSL
        }
    }
}

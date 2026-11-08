Shader "CardboardXR/Demos/AsciiTerminal"
{
    Properties
    {
        _GlyphAtlas ("Glyph Atlas", 2D) = "white" {}
        _GlyphCount ("Glyph Count", Float) = 70
        _Cell ("Cell Size (px)", Vector) = (12, 20, 0, 0)
        _Ink ("Phosphor", Color) = (0.25, 1, 0.45, 1)
        _Paper ("Background", Color) = (0.01, 0.04, 0.02, 1)
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
            CBUFFER_END

            static const float3 k_Light = float3(0.25, 0.9, 0.3);

            // The room pass writes one green. Hands and the door are not that hue.
            bool AsciiGreen(half3 color)
            {
                return color.g > 0.25 && color.r < color.g * 0.22 && color.b < color.g * 0.22;
            }

            // Angels write equal channels above 1. A white hand sits at 1 and is left alone.
            bool AsciiAngel(half3 color)
            {
                return color.r > 1.15 && color.g > 1.15 && color.b > 1.15
                    && abs(color.r - color.g) < 0.25 * color.g
                    && abs(color.g - color.b) < 0.25 * color.g;
            }

            // Mirrors AsciiGlyph.DepthBrightness.
            float DepthBrightness(float eyeDepth)
            {
                float d = max(eyeDepth, 0.0);
                float falloff = 1.0 / (1.0 + d * d * 0.55);
                return lerp(0.08, 1.0, falloff);
            }

            // Mirrors AsciiGlyph.CellScale / CellSize. Near tiles are 4x the far tiles, in screen pixels.
            float2 CellPixels(float eyeDepth)
            {
                float scale = eyeDepth < 1.05 ? 4.0 : eyeDepth < 2.6 ? 2.0 : 1.0;
                return float2(8.0, 12.0) * scale;
            }

            // Mirrors AsciiGlyph.GlyphPick. Depth chooses the band; form only slides inside it.
            float GlyphPick(float form, float depthBrightness)
            {
                float depth = saturate(depthBrightness);
                float low = lerp(0.0, 0.72, depth);
                float high = lerp(0.12, 1.0, depth);
                return lerp(low, high, saturate(form));
            }

            float Eye(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            // March toward the light in world space and reproject. A nearer surface along that
            // ray is a cast shadow: the table on the floor, the angel on the ground.
            float CastShadow(float2 uv)
            {
                float raw = SampleSceneDepth(uv);
                float3 pos = ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
                float3 lightDir = normalize(k_Light);
                float occ = 0.0;
                [unroll]
                for (int i = 1; i <= 8; i++)
                {
                    float3 samplePos = pos + lightDir * (0.14 * i);
                    float3 ndc = ComputeNormalizedDeviceCoordinatesWithZ(samplePos, UNITY_MATRIX_VP);
                    if (ndc.x < 0.0 || ndc.x > 1.0 || ndc.y < 0.0 || ndc.y > 1.0)
                        continue;
                    float rayEye = LinearEyeDepth(ndc.z, _ZBufferParams);
                    float sceneEye = LinearEyeDepth(SampleSceneDepth(ndc.xy), _ZBufferParams);
                    float gap = rayEye - sceneEye;
                    if (gap > 0.07 && gap < 2.2)
                        occ = 1.0;
                }
                return occ;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float rawDepth = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
                if (rawDepth <= 1e-5)
#else
                if (rawDepth >= 0.99999)
#endif
                    return half4(0.0, 0.0, 0.0, 1.0);

                float2 size = max(_BlitTexture_TexelSize.zw, float2(1.0, 1.0));
                float2 pixel = uv * size;

                // Screen grid, not the surface's UVs, so every character faces the camera.
                // One refinement so the whole tile shares the depth at its center.
                float2 cell = CellPixels(Eye(uv));
                float2 cellCoord = floor(pixel / cell);
                float2 centerUv = ((cellCoord + 0.5) * cell) / size;
                float depth = Eye(centerUv);
                cell = CellPixels(depth);
                cellCoord = floor(pixel / cell);
                float2 inCell = pixel / cell - cellCoord;
                centerUv = ((cellCoord + 0.5) * cell) / size;
                depth = Eye(centerUv);

                half4 src = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, centerUv, 0);
                bool angel = AsciiAngel(src.rgb);
                if (!angel && !AsciiGreen(src.rgb))
                    return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);

                // Angel signal is 1.5..4. Room green is 0.45..1. Both carry the light terminator.
                float form = angel ? saturate((src.g - 1.5) / 2.5) : saturate((src.g - 0.45) / 0.55);

                float2 cellUv = cell / size;
                float left = Eye(centerUv - float2(cellUv.x, 0.0));
                float right = Eye(centerUv + float2(cellUv.x, 0.0));
                float down = Eye(centerUv - float2(0.0, cellUv.y));
                float up = Eye(centerUv + float2(0.0, cellUv.y));
                float curv = max(abs(left + right - 2.0 * depth), abs(down + up - 2.0 * depth));
                float edge = saturate((curv / max(depth, 0.3) - 0.18) * 4.0);
                // Cast shadow from the ray, plus the dark side of the mesh. Size stays on depth.
                float shade = max(CastShadow(centerUv), saturate(1.0 - form));

                float depthBright = saturate(DepthBrightness(depth) + edge * 0.2);
                float pick = GlyphPick(form, depthBright);
                pick = saturate(pick * lerp(1.0, 0.62, shade) + edge * 0.12);
                float bright = depthBright * lerp(1.0, 0.18, shade);

                float count = max(_GlyphCount, 1.0);
                float index = floor(pick * (count - 1.0) + 1e-4);
                float2 atlasScale = float2(1.0 / count, 1.0);
                float2 atlasUV = float2((index + inCell.x) / count, inCell.y);
                float2 grid = pixel / cell;
                half glyph = SAMPLE_TEXTURE2D_GRAD(_GlyphAtlas, sampler_GlyphAtlas, atlasUV,
                    ddx(grid) * atlasScale, ddy(grid) * atlasScale).r;

                half cover = saturate(glyph * lerp(0.65, 1.0, saturate(bright)));
                // Room ink stays green and dims with distance. Angels stay white and above 1,
                // brighter up close, so they still read as distance without leaving the phosphor.
                half3 ink = angel
                    ? half3(5.0, 5.0, 5.0) * lerp(0.55, 1.0, saturate(DepthBrightness(depth))) * lerp(0.6, 1.0, 1.0 - shade)
                    : _Ink.rgb * bright;
                half3 paper = angel ? half3(0.02, 0.02, 0.025) : _Paper.rgb;
                return half4(lerp(paper, ink, cover), 1.0);
            }
            ENDHLSL
        }
    }
}

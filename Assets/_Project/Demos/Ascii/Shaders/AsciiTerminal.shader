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
            #include "AsciiGlyphs.hlsl"

            TEXTURE2D(_GlyphAtlas);
            SAMPLER(sampler_GlyphAtlas);

            CBUFFER_START(UnityPerMaterial)
                float4 _GlyphAtlas_ST;
                float _GlyphCount;
                float4 _Cell;
                half4 _Ink;
                half4 _Paper;
            CBUFFER_END

            // Set by AsciiDemo and AsciiPeople. The camera image keeps updating with the
            // passthrough screen off; its frustum maps a view direction to its UV.
            TEXTURE2D(_AsciiCameraImage);
            TEXTURE2D(_AsciiPersonMask);
            TEXTURE2D(_AsciiPersonMaskPrev);
            float4 _AsciiCameraFrustum;
            float _AsciiCameraOn;
            float _AsciiPersonOn;
            float _AsciiPersonBlend;
            // 1 inside, where passthrough is off: nothing rendered means the void. Outside, those
            // pixels are the passthrough image.
            float _AsciiVoid;
            // 0 to 1 as the player turns to stone: every glyph drains to gray.
            float _AsciiStone;
            static const float3 k_Light = float3(0.25, 0.9, 0.3);

            // The room pass writes one green. Hands and the door are not that hue.
            bool AsciiGreen(half3 color)
            {
                return color.g > 0.25 && color.r < color.g * 0.22 && color.b < color.g * 0.22;
            }

            // The doorway from inside writes magenta: the real room goes there.
            bool AsciiWindow(half3 color)
            {
                return color.r > 0.8 && color.b > 0.8 && color.g < 0.15;
            }

            float Eye(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            // Eye depth at uv, or fallback where nothing was rendered.
            float NearOr(float2 uv, float fallback)
            {
                float raw = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
                bool empty = raw <= 1e-5;
#else
                bool empty = raw >= 0.99999;
#endif
                return empty ? fallback : LinearEyeDepth(raw, _ZBufferParams);
            }

            // The camera image along this pixel's view ray. With the screen off the AR camera
            // stops moving, so the eye's own direction stands in for it.
            float2 CameraUv(float2 uv, out bool inside)
            {
                float3 world = ComputeWorldSpacePosition(uv, 0.5, UNITY_MATRIX_I_VP);
                float3 view = TransformWorldToView(world);
                float2 dir = view.xy / max(-view.z, 1e-4);
                float2 imageUv = (dir - _AsciiCameraFrustum.xy) / max(_AsciiCameraFrustum.zw, 1e-4) + 0.5;
                inside = all(imageUv >= 0.0) && all(imageUv <= 1.0);
                return imageUv;
            }

            half3 CameraColor(float2 uv)
            {
                bool inside;
                float2 imageUv = CameraUv(uv, inside);
                if (_AsciiCameraOn < 0.5 || !inside)
                    return 0;
                return SAMPLE_TEXTURE2D_LOD(_AsciiCameraImage, sampler_LinearClamp, imageUv, 0).rgb;
            }

            half PersonCover(float2 uv)
            {
                if (_AsciiPersonOn <= 0.0 || _AsciiCameraOn < 0.5)
                    return 0;
                bool inside;
                float2 imageUv = CameraUv(uv, inside);
                if (!inside)
                    return 0;
                half previous = SAMPLE_TEXTURE2D_LOD(_AsciiPersonMaskPrev, sampler_LinearClamp, imageUv, 0).r;
                half current = SAMPLE_TEXTURE2D_LOD(_AsciiPersonMask, sampler_LinearClamp, imageUv, 0).r;
                return smoothstep(0.3, 0.7, lerp(previous, current, saturate(_AsciiPersonBlend))) * _AsciiPersonOn;
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
                    float hit = saturate((gap - 0.04) * 2.5) * saturate((2.2 - gap) * 1.5);
                    occ = max(occ, hit);
                }
                return occ;
            }

            struct Tile
            {
                float2 cell;
                float2 inCell;
                float2 centerUv;
            };

            // Screen grid, not the surface's UVs, so every character faces the camera. Near or far
            // is decided per 12x20 block, so a big tile never splits into small pieces.
            Tile TileAt(float2 pixel, float2 size)
            {
                Tile tile;
                float2 block = floor(pixel / k_AsciiNearCell);
                tile.cell = AsciiCellPixels(Eye(((block + 0.5) * k_AsciiNearCell) / size));
                float2 cellCoord = floor(pixel / tile.cell);
                tile.inCell = pixel / tile.cell - cellCoord;
                tile.centerUv = ((cellCoord + 0.5) * tile.cell) / size;
                return tile;
            }

            // A person in the room, as light-blue glyphs: the camera's brightness inside the mask
            // chooses the letters, so the figure keeps its shape and shading without breaking the
            // terminal. Tiles on the outline get denser letters so the silhouette reads.
            half3 Person(Tile tile, half cover)
            {
                half3 camera = CameraColor(tile.centerUv);
                half lum = dot(camera, half3(0.299, 0.587, 0.114));
                half rim = 1.0 - abs(cover * 2.0 - 1.0);
                float pick = saturate(lerp(0.3, 0.95, lum) + rim * 0.25);
                half glyph = AsciiGlyphInk(TEXTURE2D_ARGS(_GlyphAtlas, sampler_GlyphAtlas), _GlyphCount, pick, tile.inCell, tile.cell);
                half3 ink = half3(0.55, 0.85, 1.0) * lerp(0.55, 1.0, lum);
                half3 paper = half3(0.01, 0.025, 0.05);
                return lerp(paper, ink, glyph);
            }

            // Green at the tile center, or any corner — fills the jagged triangle edges of AR
            // plane meshes so wall borders still become glyphs instead of raw passthrough.
            // `at` is where the green was found, for its depth: at a doorway edge the center can be
            // outside the opening, with nothing rendered there.
            bool RoomCover(float2 centerUv, float2 cellUv, out half3 sample, out float2 at)
            {
                at = centerUv;
                half4 src = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, centerUv, 0);
                sample = src.rgb;
                if (AsciiGreen(sample))
                    return true;
                float2 o = cellUv * 0.42;
                float2 ua = centerUv + float2(-o.x, -o.y);
                float2 ub = centerUv + float2(o.x, -o.y);
                float2 uc = centerUv + float2(-o.x, o.y);
                float2 ud = centerUv + float2(o.x, o.y);
                half3 a = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, ua, 0).rgb;
                half3 b = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, ub, 0).rgb;
                half3 c = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uc, 0).rgb;
                half3 d = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, ud, 0).rgb;
                if (AsciiGreen(a)) { sample = a; at = ua; return true; }
                if (AsciiGreen(b)) { sample = b; at = ub; return true; }
                if (AsciiGreen(c)) { sample = c; at = uc; return true; }
                if (AsciiGreen(d)) { sample = d; at = ud; return true; }
                return false;
            }

            half3 Room(Tile tile, float2 size, out bool drawn)
            {
                float2 cell = tile.cell;
                float2 inCell = tile.inCell;
                float2 centerUv = tile.centerUv;
                float2 cellUv = cell / size;

                half3 sample;
                float2 at;
                drawn = RoomCover(centerUv, cellUv, sample, at);
                if (!drawn)
                    return 0;
                float depth = Eye(at);

                float form = saturate((sample.g - 0.45) / 0.55);

                // Past the doorway's edge there's no depth at all; that's the portal's cut, not a
                // silhouette, so it mustn't light the rim.
                float left = NearOr(centerUv - float2(cellUv.x, 0.0), depth);
                float right = NearOr(centerUv + float2(cellUv.x, 0.0), depth);
                float down = NearOr(centerUv - float2(0.0, cellUv.y), depth);
                float up = NearOr(centerUv + float2(0.0, cellUv.y), depth);
                float curv = max(abs(left + right - 2.0 * depth), abs(down + up - 2.0 * depth));
                float edge = saturate((curv / max(depth, 0.3) - 0.18) * 4.0);
                // Cast shadow from the ray, plus the dark side of the mesh. Size stays on depth.
                float shade = max(CastShadow(centerUv), saturate(1.0 - form));

                // Edges stay inside the fade, so a far silhouette doesn't glow out of the black.
                float presence = AsciiDepthBrightness(depth);
                float depthBright = saturate(presence + edge * 0.2 * presence);
                float pick = AsciiGlyphPick(form, depthBright);
                pick = saturate(pick * lerp(1.0, 0.62, shade) + edge * 0.12);
                float bright = depthBright * lerp(1.0, 0.18, shade);

                half glyph = AsciiGlyphInk(TEXTURE2D_ARGS(_GlyphAtlas, sampler_GlyphAtlas), _GlyphCount, pick, inCell, cell);
                half cover = saturate(glyph * lerp(0.82, 1.0, saturate(bright)));
                half3 ink = _Ink.rgb * bright;
                half3 paper = _Paper.rgb * presence;
                return lerp(paper, ink, cover);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float2 size = max(_BlitTexture_TexelSize.zw, float2(1.0, 1.0));
                float2 pixel = uv * size;

                float rawDepth = SampleSceneDepth(uv);
#if UNITY_REVERSED_Z
                bool empty = rawDepth <= 1e-5;
#else
                bool empty = rawDepth >= 0.99999;
#endif

                half3 here = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
                float3 eye = _WorldSpaceCameraPos;
                float3 dir = normalize(ComputeWorldSpacePosition(uv, 0.5, UNITY_MATRIX_I_VP) - eye);

                // Outside the gate: how much of this pixel looks through the doorway, antialiased
                // on the opening's true edge rather than snapped to whole glyph tiles.
                float portal = 1.0;
                if (_AsciiPortalClip > 0.5)
                {
                    float inside = AsciiDoorInside(eye, eye + dir * 100.0);
                    portal = saturate(inside / max(fwidth(inside), 1e-5) + 0.5);
                    if (portal <= 0.0)
                        return half4(here, 1.0);
                }

                // The doorway back is the real room, people included.
                if (!empty && AsciiWindow(here))
                    return half4(CameraColor(uv), 1.0);

                Tile tile = TileAt(pixel, size);
                half3 color;
                bool drawn = false;
                color = Room(tile, size, drawn);
                // No scene depth (nothing rendered) and no room in this tile: true black inside the
                // dimension, and through the doorway from outside, not the clear color or the
                // passthrough behind it. A room tile keeps its whole glyph even where its pixels
                // fall between plane meshes or past the doorway's edge.
                if (!drawn && empty)
                    color = _AsciiVoid > 0.5 || _AsciiPortalClip > 0.5 ? half3(0, 0, 0) : here;
                // Angels already drew their own glyphs; hands and UI pass straight through.
                else if (!drawn)
                    color = here;

                // People in the real room become blue glyphs, so nobody gets walked into. One
                // test per tile keeps whole letters; the narrow band keeps a tile room or person.
                // A UI panel in front hides them: it's drawn after this pass, and its glass would
                // otherwise let the bright figure through as if it stood on top.
                half cover = PersonCover(tile.centerUv);
                half person = smoothstep(0.35, 0.65, cover);
                if (person > 0.0)
                    person *= 1.0 - AsciiPanelCover(eye, dir);
                if (person > 0.0)
                    color = lerp(color, Person(tile, cover), person);
                if (_AsciiStone > 0.0)
                {
                    half luma = dot(color, half3(0.299, 0.587, 0.114));
                    color = lerp(color, half3(0.62, 0.6, 0.56) * luma * 1.6, _AsciiStone);
                }
                return half4(lerp(here, color, portal), 1.0);
            }
            ENDHLSL
        }
    }
}

Shader "CardboardXR/Demos/AsciiSurface"
{
    Properties
    {
        _GlyphAtlas ("Glyph Atlas", 2D) = "white" {}
        _GlyphCount ("Glyph Count", Float) = 70
        _CellSize ("Cell Size (m)", Float) = 0.08
        _Color ("Phosphor (no camera)", Color) = (0.25, 1, 0.45, 1)
        _BgColor ("Background", Color) = (0.01, 0.01, 0.01, 1)
        _Extrude ("Vertex Extrude (m)", Float) = 0.004
        _ColorBoost ("Camera Color Boost", Float) = 1.5
        _BgFromCamera ("Camera Color In Background", Float) = 0.12
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "AsciiSurface"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex AsciiVert
            #pragma fragment AsciiFrag
            #pragma multi_compile_instancing

            #include "AsciiShader.hlsl"
            ENDHLSL
        }
    }
}

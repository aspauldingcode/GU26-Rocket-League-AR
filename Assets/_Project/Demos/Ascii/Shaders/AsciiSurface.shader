Shader "CardboardXR/Demos/AsciiSurface"
{
    Properties
    {
        _RoomColor ("Room", Color) = (0.15, 0.85, 0.32, 1)
        _FurnitureColor ("Furniture", Color) = (1, 0.78, 0.08, 1)
        _ForceFurniture ("Force Furniture", Float) = 0
        _Extrude ("Vertex Extrude (m)", Float) = 0.01
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
            Name "AsciiShade"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On
            ZTest LEqual
            Stencil
            {
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex AsciiVert
            #pragma fragment AsciiFrag
            #pragma multi_compile_instancing

            #include "AsciiShader.hlsl"
            ENDHLSL
        }
    }
}

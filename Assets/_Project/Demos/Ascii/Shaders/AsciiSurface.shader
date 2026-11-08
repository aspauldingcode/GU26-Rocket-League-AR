Shader "CardboardXR/Demos/AsciiSurface"
{
    Properties
    {
        _RoomColor ("Room", Color) = (0, 1, 0, 1)
        _FurnitureColor ("Furniture", Color) = (1, 0.92, 0, 1)
        _ForceFurniture ("Force Furniture", Float) = 0
        _UseMeshNormal ("Use Mesh Normal", Float) = 0
        _Volume ("Volume Classify", Float) = 0
        _Figure ("Figure", Float) = 0
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

            HLSLPROGRAM
            #pragma vertex AsciiVert
            #pragma fragment AsciiFrag
            #pragma multi_compile_instancing

            #include "AsciiShader.hlsl"
            ENDHLSL
        }
    }
}

/*
Chris Nightingale, 2025

BVH point cloud renderer.

Replaces the geometry shader approach with StructuredBuffer + vertex shader,
which runs identically on Android (Quest 3+) and desktop — no geometry shader
required, no fallback path needed.

Points are driven via DrawProcedural from OctreeRenderer. Each draw call
covers one BVH node; _Points is set per-node via MaterialPropertyBlock.

_Points is a StructuredBuffer<uint3> (12 bytes/point):
  .x = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
  .y = RGB24 in bits 0-23            — bits 24-31 unused/spare
  .z = uint16_z in bits 0-15         — Z quantized [0,65535], upper 16 bits spare
*/

Shader "StoryLab PointCloud/URP Octree"
{
    Properties
    {
        _PointSize("Point Size", Float) = 0.02
        [KeywordEnum(Vertex, Solid, Blend)] _ColorMode("Color Mode", int) = 0
        _Color("Color", Color) = (1,1,1,1)
        _ColorBlend("Color Blend", Range(0,1)) = 0
        [KeywordEnum(Diamond, Circle, Square)] _PointShape("Point Shape", int) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100
        Cull Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PointSize;
                half4 _Color;
                float _ColorBlend;
            CBUFFER_END

            // Set per-node via MaterialPropertyBlock.
            // uint3 per point (12 bytes, one cache line):
            //   .x = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
            //   .y = RGB24 in bits 0-23            — bits 24-31 unused/spare
            //   .z = uint16_z in bits 0-15         — Z quantized [0,65535], upper 16 bits spare
            StructuredBuffer<uint3> _Points;
            float3 _BoundsMin;
            float3 _BoundsSize;
            float _LodScale; // _PointSize * lodScale * 0.5 — extent multiplied by P._m00/11 in shader

            // Square/circle: axis-aligned quad. Corner order TL, BL, BR, TR.
            static const float2 _CornerOffset[4] = { float2(-1,1), float2(-1,-1), float2(1,-1), float2(1,1) };
            // Diamond: rotated 45° — corners at cardinal points (top, left, bottom, right).
            // Geometry IS the diamond shape; no fragment clip required.
            static const float2 _DiamondOffset[4] = { float2(0,1), float2(-1,0), float2(0,-1), float2(1,0) };

            struct a2v
            {
                uint vertexID : SV_VertexID;
            };

            struct v2f
            {
                float4 clipPos : SV_POSITION;
                half3  color   : COLOR;

                UNITY_VERTEX_OUTPUT_STEREO

            #if _POINTSHAPE_CIRCLE
                half2  uv      : TEXCOORD0;
            #endif

            #if !_COLORMODE_SOLID
            #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                half fogCoord : TEXCOORD1;
            #endif
            #endif
            };

            // sRGB → linear (used when Unity is in linear colour space)
            float3 GammaToLinearSpace(float3 sRGB)
            {
                return sRGB * (sRGB * (sRGB * 0.305306011 + 0.682171111) + 0.012522878);
            }

            v2f vert(a2v input)
            {
                uint3 pt     = _Points[input.vertexID / 4];
                uint  corner = input.vertexID % 4;
            #if _POINTSHAPE_DIAMOND
                float2 offset = _DiamondOffset[corner];
            #else
                float2 offset = _CornerOffset[corner];
            #endif

                // Dequantize position from uint16 unorm to world space.
                float3 pos = _BoundsMin + _BoundsSize * float3(
                    (pt.x & 0xFFFFu) * (1.0 / 65535.0),
                    (pt.x >> 16)     * (1.0 / 65535.0),
                    (pt.z & 0xFFFFu) * (1.0 / 65535.0));

                // Unpack RGB from bits 0-23 of pt.y (bits 24-31 unused; alpha dropped at build time).
                half3 color = half3(
                     pt.y        & 0xFFu,
                    (pt.y >>  8) & 0xFFu,
                    (pt.y >> 16) & 0xFFu
                ) * (1.0h / 255.0h);

                float4 clipPos = TransformWorldToHClip(mul(UNITY_MATRIX_M, float4(pos, 1.0)).xyz);

                // Compute per-eye extent using the patched stereo projection matrix.
                // UNITY_MATRIX_P is set per-eye by URP in single-pass instanced mode,
                // so this is correct for both eyes without any CPU-side per-eye work.
                float2 screenExtent = float2(
                    abs(UNITY_MATRIX_P._m00),
                    abs(UNITY_MATRIX_P._m11)) * _LodScale;

                // 1px minimum: 2/screenHeight in NDC (clip.w ≈ 1 at typical VR distances, close enough).
                float minExtent = 2.0 / _ScreenParams.y;
                screenExtent = max(screenExtent, minExtent);

                clipPos.xy += offset * screenExtent;

                v2f o;
                ZERO_INITIALIZE(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.clipPos = clipPos;
                o.color   = color;
            #if _POINTSHAPE_CIRCLE
                o.uv = offset;
            #endif

            #if !_COLORMODE_SOLID
            #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                o.fogCoord = ComputeFogFactor(clipPos.z);
            #endif
            #endif

                return o;
            }

            half4 frag(v2f i) : SV_TARGET
            {
            #if _POINTSHAPE_CIRCLE
                clip(1.0 - length(i.uv));
            #endif

                #if _COLORMODE_SOLID
                    return _Color;
                #else
                    #if _COLORMODE_BLEND
                        half3 outColor = lerp(i.color, _Color.rgb, _ColorBlend);
                    #else
                        half3 outColor = i.color;
                    #endif

                    #ifndef UNITY_COLORSPACE_GAMMA
                        outColor = GammaToLinearSpace(outColor);
                    #endif

                    #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                        outColor = MixFog(outColor, i.fogCoord);
                    #endif

                    return half4(outColor, 1);
                #endif
            }

            half4 depthFrag(v2f i) : SV_TARGET
            {
            #if _POINTSHAPE_CIRCLE
                clip(1.0 - length(i.uv));
            #endif
                return 0;
            }

        ENDHLSL

        Pass
        {
            Name "Universal Forward"
            Tags
            {
                "LightMode" = "UniversalForward"
                "RenderType" = "Opaque"
                "UniversalMaterialType" = "Unlit"
                "Queue" = "Geometry"
            }

            HLSLPROGRAM
                #pragma target 4.5
                #pragma multi_compile_fog
                #pragma multi_compile _ UNITY_COLORSPACE_GAMMA
                #pragma shader_feature _COLORMODE_VERTEX _COLORMODE_SOLID _COLORMODE_BLEND
                #pragma shader_feature _POINTSHAPE_DIAMOND _POINTSHAPE_CIRCLE _POINTSHAPE_SQUARE
                #pragma vertex vert
                #pragma fragment frag
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
                "RenderType" = "Opaque"
                "UniversalMaterialType" = "Unlit"
                "Queue" = "Geometry"
            }

            HLSLPROGRAM
                #pragma target 4.5
                #pragma multi_compile _ UNITY_COLORSPACE_GAMMA
                #pragma shader_feature _POINTSHAPE_DIAMOND _POINTSHAPE_CIRCLE _POINTSHAPE_SQUARE
                #pragma vertex vert
                #pragma fragment depthFrag
            ENDHLSL
        }
    }
    // No fallback — Quest 3+ (Vulkan) and PC both support StructuredBuffers in vertex shaders
    CustomEditor "StoryLabResearch.PointCloud.PointCloudShaderGUI"
}

/*
Chris Nightingale, 2025

Octree point cloud renderer.

Replaces the geometry shader approach with StructuredBuffer + vertex shader,
which runs identically on Android (Quest 3+) and desktop — no geometry shader
required, no fallback path needed.

Points are driven via DrawProcedural from OctreeRenderer. Each draw call
covers one octree node; _Points is set per-node via MaterialPropertyBlock.

_Points is a StructuredBuffer<uint4>: xyz = position floats (asuint reinterpret),
w = (octant[3]<<29) | (alpha5[5]<<24) | RGB[24]. One 16-byte cache-line fetch per point.
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
                float _LodSizeScale;
                half4 _Color;
                float _ColorBlend;
            CBUFFER_END

            // Set per-node via MaterialPropertyBlock.
            // uint3 per point (12 bytes, one cache line):
            //   .x = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
            //   .y = (octant[3] << 24) | RGB24     — octant in bits 24-26, RGB in bits 0-23
            //   .z = uint16_z in bits 0-15         — Z quantized [0,65535]
            StructuredBuffer<uint3> _Points;
            float3 _BoundsMin;
            float3 _BoundsSize;
            int _ActiveOctantMask; // bitmask: bit o set = draw octant o

            // Square quad: two triangles covering [-1,1]^2 UV space
            static const float2 _CornerUV[4]  = { float2(-1,1), float2(-1,-1), float2(1,-1), float2(1,1) };
            static const uint   _CornerMap[6]  = { 0, 1, 2, 0, 2, 3 };

            struct a2v
            {
                uint vertexID : SV_VertexID;
            };

            struct v2f
            {
                float4 clipPos : SV_POSITION;
                half3  color   : COLOR;
                half2  uv      : TEXCOORD0;

                UNITY_VERTEX_OUTPUT_STEREO

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
                v2f o;
                ZERO_INITIALIZE(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                uint pointIdx = input.vertexID / 6;

                // Single fetch: position + color + octant in one uint3 (12 bytes, one cache line).
                uint3 pt = _Points[pointIdx];

                // Octant in bits 24-26 of pt.y — check mask before any ALU work.
                uint octant = (pt.y >> 24) & 0x7u;
                if ((_ActiveOctantMask & (1u << octant)) == 0u)
                    return o; // SV_POSITION = (0,0,0,0) → degenerate, clipped for free

                uint corner = _CornerMap[input.vertexID % 6];
                float2 uv   = _CornerUV[corner];

                // Dequantize position from uint16 unorm to world space.
                float3 pos = _BoundsMin + _BoundsSize * float3(
                    (pt.x & 0xFFFFu) * (1.0 / 65535.0),
                    (pt.x >> 16)     * (1.0 / 65535.0),
                    (pt.z & 0xFFFFu) * (1.0 / 65535.0));

                // Unpack RGB from bits 0-23 of pt.y (alpha is gone — size driven by _LodSizeScale).
                half3 color = half3(
                     pt.y        & 0xFFu,
                    (pt.y >>  8) & 0xFFu,
                    (pt.y >> 16) & 0xFFu
                ) * (1.0h / 255.0h);

                float4 clipPos = TransformWorldToHClip(mul(UNITY_MATRIX_M, float4(pos, 1.0)).xyz);

                float radius = _PointSize * _LodSizeScale * 0.5;
                float2 extent = abs(UNITY_MATRIX_P._11_22 * radius);
                clipPos.xy += uv * extent;

                o.clipPos = clipPos;
                o.color   = color; // half3 RGB, no alpha
                o.uv      = uv;

            #if !_COLORMODE_SOLID
            #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                o.fogCoord = ComputeFogFactor(clipPos.z);
            #endif
            #endif

                return o;
            }

            void ClipToShape(half2 uv)
            {
            #if _POINTSHAPE_DIAMOND
                clip(1.0 - abs(uv.x) - abs(uv.y));
            #elif _POINTSHAPE_CIRCLE
                clip(1.0 - length(uv));
            #endif
                // _POINTSHAPE_SQUARE: full quad, no clip
            }

            half4 frag(v2f i) : SV_TARGET
            {
                ClipToShape(i.uv);

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
                ClipToShape(i.uv);
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

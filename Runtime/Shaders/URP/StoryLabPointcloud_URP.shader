/*
Chris Nightingale, 2025

BVH point cloud renderer.

Replaces the geometry shader approach with StructuredBuffer + vertex shader,
which runs identically on Android (Quest 3+) and desktop — no geometry shader
required, no fallback path needed.

Points are driven via DrawProcedural from PointCloudRenderer. Each draw call
covers one BVH node; _Points is set per-node via MaterialPropertyBlock.

_Points is a StructuredBuffer<uint3> (12 bytes/point):
  .x = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
  .y = RGB24 in bits 0-23            — bits 24-31 unused/spare
  .z = uint16_z in bits 0-15         — Z quantized [0,65535], upper 16 bits spare
*/

Shader "StoryLab Point Cloud/StoryLabPointcloud_URP"
{
    Properties
    {
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
                half4 _Color;
                float _ColorBlend;
            CBUFFER_END

            // Global point buffer shared across all nodes. uint3 per point (12 bytes):
            //   .x = (uint16_y << 16) | uint16_x  — XY quantized [0,65535] relative to node bounds
            //   .y = RGB24 in bits 0-23            — bits 24-31 unused/spare
            //   .z = uint16_z in bits 0-15         — Z quantized [0,65535], upper 16 bits spare
            ByteAddressBuffer _Points;

            // Per-selected-node descriptors. Each entry is 3 float4s (48 bytes, stride=48):
            //   [0] float4: boundsMin.xyz, lodScale
            //   [1] float4: boundsSize.xyz, <pad>
            //   [2] uint4:  pointOffset, pointCount, 0, 0  (stored as raw uint bits)
            // Indexed by SV_InstanceID — one instance per selected node.
            StructuredBuffer<float4> _NodeDescriptors;

            // Two triangles per point (6 verts). Tri 0: TL,BL,BR  Tri 1: TL,BR,TR.
            // Square/circle corners: TL, BL, BR, TR.
            static const float2 _CornerOffset[6] = {
                float2(-1, 1), float2(-1,-1), float2( 1,-1),
                float2(-1, 1), float2( 1,-1), float2( 1, 1)
            };
            // Diamond: rotated 45°. Top, Left, Bottom, Right → same two-triangle split.
            static const float2 _DiamondOffset[6] = {
                float2( 0, 1), float2(-1, 0), float2( 0,-1),
                float2( 0, 1), float2( 0,-1), float2( 1, 0)
            };

            struct a2v
            {
                uint vertexID   : SV_VertexID;
                uint instanceID : SV_InstanceID; // always present; UNITY_SETUP_INSTANCE_ID decodes eye+node under stereo
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
                UNITY_SETUP_INSTANCE_ID(input);         // sets unity_InstanceID and unity_StereoEyeIndex

                // Look up this instance's node descriptor (3 consecutive float4s).
                // Under single-pass instanced stereo, Unity packs [nodeIndex*2 + eyeIndex] into
                // SV_InstanceID; UNITY_SETUP_INSTANCE_ID extracts the real node index into
                // unity_InstanceID via >> 1. When instancing is disabled, fall back to raw SV_InstanceID.
                // Under stereo instancing, UNITY_SETUP_INSTANCE_ID has decoded input.instanceID into
                // unity_InstanceID (stripping the eye bit). Use that when available; otherwise
                // input.instanceID is already the plain node index.
                #if UNITY_ANY_INSTANCING_ENABLED
                uint nodeIndex = unity_InstanceID;
                #else
                uint nodeIndex = input.instanceID;
                #endif
                uint base3      = nodeIndex * 3u;
                float4 descA    = _NodeDescriptors[base3 + 0u]; // boundsMin.xyz, lodScale
                float4 descB    = _NodeDescriptors[base3 + 1u]; // boundsSize.xyz, <pad>
                float4 descC    = _NodeDescriptors[base3 + 2u]; // pointOffset, pointCount (as uint bits)

                uint pointOffset = asuint(descC.x);
                uint pointCount  = asuint(descC.y);
                uint localIndex  = input.vertexID / 6u;

                // Discard vertices that exceed this node's point count (padding from maxPointCount).
                v2f o;
                ZERO_INITIALIZE(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                if (localIndex >= pointCount)
                {
                    o.clipPos = float4(2, 2, 2, 1); // outside clip space — discarded by rasterizer
                    return o;
                }

                uint byteAddr = (pointOffset + localIndex) * 12u;
                uint3 pt      = _Points.Load3(byteAddr);
                uint  corner  = input.vertexID % 6u;
            #if _POINTSHAPE_DIAMOND
                float2 offset = _DiamondOffset[corner];
            #else
                float2 offset = _CornerOffset[corner];
            #endif

                float3 boundsMin  = descA.xyz;
                float3 boundsSize = descB.xyz;
                float  lodScale   = descA.w;

                // Dequantize position from uint16 unorm to world space.
                float3 pos = boundsMin + boundsSize * float3(
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
                    abs(UNITY_MATRIX_P._m11)) * lodScale;

                // 1px minimum: 2/screenHeight in NDC (clip.w ≈ 1 at typical VR distances, close enough).
                float minExtent = 2.0 / _ScreenParams.y;
                screenExtent = max(screenExtent, minExtent);

                clipPos.xy += offset * screenExtent;

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
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
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
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
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
                #pragma multi_compile_instancing
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
                #pragma multi_compile_instancing
                #pragma shader_feature _POINTSHAPE_DIAMOND _POINTSHAPE_CIRCLE _POINTSHAPE_SQUARE
                #pragma vertex vert
                #pragma fragment depthFrag
            ENDHLSL
        }
    }
    // No fallback — Quest 3+ (Vulkan) and PC both support StructuredBuffers in vertex shaders
    CustomEditor "StoryLabResearch.PointCloud.PointCloudShaderGUI"
}

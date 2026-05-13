/*
Chris Nightingale, 2025

Octree point cloud renderer.

Replaces the geometry shader approach with StructuredBuffer + vertex shader,
which runs identically on Android (Quest 3+) and desktop — no geometry shader
required, no fallback path needed.

Points are driven via DrawProcedural from OctreeRenderer. Each draw call
covers one octree node; _Positions and _ColorsPacked are set per-node via
MaterialPropertyBlock.

Point positions are in world space. Colors are packed RGBA8 (uint), with
alpha used as a size multiplier (0–255 = 0–1 range, matching the original
vertex colour convention).
*/

Shader "StoryLab PointCloud/URP Octree"
{
    Properties
    {
        _PointSize("Point Size", Float) = 0.02
        _LodSizeScale("LOD Size Scale", Float) = 1.0
        [KeywordEnum(Vertex, Solid, Blend)] _ColorMode("Color Mode", int) = 0
        _Color("Color", Color) = (1,1,1,1)
        _ColorBlend("Color Blend", Range(0,1)) = 0
        [KeywordEnum(Diamond, Circle, Square)] _PointShape("Point Shape", int) = 0
        [Toggle(DEBUG)] _Debug("Debug Crossfade", Float) = 0
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

            // Set per-node via MaterialPropertyBlock
            StructuredBuffer<float3> _Positions;
            StructuredBuffer<uint>   _ColorsPacked;   // RGBA8: R in lowest byte, A (size multiplier) in highest
            StructuredBuffer<uint>   _OctantIndices;  // per-point octant index (0-7)
            int _ActiveOctantMask;                    // bitmask: bit o set = draw octant o

            // Square quad: two triangles covering [-1,1]^2 UV space
            static const float2 _CornerUV[4]  = { float2(-1,1), float2(-1,-1), float2(1,-1), float2(1,1) };
            static const uint   _CornerMap[6]  = { 0, 1, 2, 0, 2, 3 };

            struct a2v
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 clipPos : SV_POSITION;
                float4 color   : COLOR;
                float2 uv      : TEXCOORD0;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO

            #if !_COLORMODE_SOLID
            #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                float fogCoord : TEXCOORD1;
            #endif
            #endif
            };

            float4 UnpackColor(uint packed)
            {
                return float4(
                     packed        & 0xFF,
                    (packed >>  8) & 0xFF,
                    (packed >> 16) & 0xFF,
                    (packed >> 24) & 0xFF
                ) / 255.0;
            }

            // sRGB → linear (used when Unity is in linear colour space)
            float3 GammaToLinearSpace(float3 sRGB)
            {
                return sRGB * (sRGB * (sRGB * 0.305306011 + 0.682171111) + 0.012522878);
            }

        #if LOD_FADE_CROSSFADE
            float GetEffectiveLODFactor()
            {
                float f = unity_LODFade.x >= 0 ? unity_LODFade.x : 1 + unity_LODFade.x;
                return saturate(f * 1.5);
            }
        #endif

            v2f vert(a2v input)
            {
                v2f o;
                ZERO_INITIALIZE(v2f, o);
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                uint pointIdx = input.vertexID / 6;
                uint corner   = _CornerMap[input.vertexID % 6];
                float2 uv     = _CornerUV[corner];

                float4 color  = UnpackColor(_ColorsPacked[pointIdx]);

                // Emit a zero-area degenerate triangle for masked-out octants.
                // The GPU clips it for free with no rasterisation cost.
                uint octant = _OctantIndices[pointIdx];
                bool active = (_ActiveOctantMask & (1 << octant)) != 0;

                float4 clipPos = TransformWorldToHClip(mul(UNITY_MATRIX_M, float4(_Positions[pointIdx], 1.0)).xyz);
                if (!active) { o.clipPos = clipPos; o.color = 0; o.uv = 0; return o; }

                // Alpha channel is size multiplier (0–255 stored as 0–1).
                // _LodSizeScale compensates for reduced point density at coarser LOD levels.
                float radius = _PointSize * color.a * 255.0 * _LodSizeScale * 0.5;

            #if LOD_FADE_CROSSFADE
                radius *= GetEffectiveLODFactor();
            #endif

                float2 extent  = abs(UNITY_MATRIX_P._11_22 * radius);
                clipPos.xy    += uv * extent;

                o.clipPos = clipPos;
                o.color   = color;
                o.uv      = uv;

            #if !_COLORMODE_SOLID
            #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                o.fogCoord = ComputeFogFactor(clipPos.z);
            #endif
            #endif

                return o;
            }

            void ClipToShape(float2 uv)
            {
            #if _POINTSHAPE_DIAMOND
                clip(1.0 - abs(uv.x) - abs(uv.y));
            #elif _POINTSHAPE_CIRCLE
                clip(1.0 - length(uv));
            #endif
                // _POINTSHAPE_SQUARE: full quad, no clip
            }

            float4 frag(v2f i) : SV_TARGET
            {
                ClipToShape(i.uv);

            #if LOD_FADE_CROSSFADE && DEBUG
                return half4(1, 1, 0, 1);
            #else
                #if _COLORMODE_SOLID
                    return _Color;
                #else
                    #if _COLORMODE_BLEND
                        half3 outColor = lerp(i.color.rgb, _Color.rgb, _ColorBlend);
                    #else
                        half3 outColor = i.color.rgb;
                    #endif

                    #ifndef UNITY_COLORSPACE_GAMMA
                        outColor = GammaToLinearSpace(outColor);
                    #endif

                    #if FOG_LINEAR || FOG_EXP || FOG_EXP2
                        outColor = MixFog(outColor, i.fogCoord);
                    #endif

                    return float4(outColor, 1);
                #endif
            #endif
            }

            float4 depthFrag(v2f i) : SV_TARGET
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
                #pragma multi_compile_instancing
                #pragma shader_feature _COLORMODE_VERTEX _COLORMODE_SOLID _COLORMODE_BLEND
                #pragma shader_feature _POINTSHAPE_DIAMOND _POINTSHAPE_CIRCLE _POINTSHAPE_SQUARE
                #pragma multi_compile _ LOD_FADE_CROSSFADE
                #pragma multi_compile _ DEBUG
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
                #pragma multi_compile _ LOD_FADE_CROSSFADE
                #pragma vertex vert
                #pragma fragment depthFrag
            ENDHLSL
        }
    }
    // No fallback — Quest 3+ (Vulkan) and PC both support StructuredBuffers in vertex shaders
    CustomEditor "StoryLabResearch.PointCloud.PointCloudShaderGUI"
}

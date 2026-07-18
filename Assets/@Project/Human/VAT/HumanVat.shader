// GPU-anim Stage 1 Chunk B: VAT-sampling instanced crowd shader (URP).
// Position + normal are read from two RGBAHalf VAT textures (baked by HumanVatBaker) and the world
// transform is built per-instance from a StructuredBuffer filled by CrowdRenderer. All lit/depth/shadow
// passes share ONE VAT vertex function (VatWorld) so every pass animates identically.
//
// Consumer contract (must match Chunk A bake exactly):
//   - column u = mesh UV2.x (TEXCOORD1) = (vertexIndex+0.5)/2964 ; row v = (row+0.5)/_VatRows
//   - LoopClose: rowA = floor(phase01*rows) % rows, rowB = (rowA+1) % rows, lerp by fractional (seamless wrap)
//   - baked pos/normal are already in Human-root space at world/human scale (do NOT re-apply SMR scale)
//   - team color arrives sRGB-packed and is linearized here before use as albedo.
Shader "AF/CrowdCity/HumanVat"
{
    Properties
    {
        _PositionVat ("VAT Position", 2D) = "black" {}
        _NormalVat ("VAT Normal", 2D) = "bump" {}
        _VatRows ("VAT Rows", Float) = 21
        _Smoothness ("Smoothness", Range(0,1)) = 0.1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_PositionVat); SAMPLER(sampler_PositionVat);
        TEXTURE2D(_NormalVat);   SAMPLER(sampler_NormalVat);

        CBUFFER_START(UnityPerMaterial)
            float4 _PositionVat_ST;
            float4 _NormalVat_ST;
            float _VatRows;
            float _Smoothness;
        CBUFFER_END

        // Per-instance record. MUST match CrowdRenderer.InstanceData (C# sequential, stride 28 bytes).
        struct VatInstance
        {
            float3 pos;         // world position of the Human root (feet), Y = groundY
            float  yaw;         // Y-rotation in radians
            float  scale;       // uniform scale
            float  phase01;     // walk-cycle phase in [0,1)
            uint   packedColor; // sRGB team color: R=bits0-7, G=8-15, B=16-23
        };
        StructuredBuffer<VatInstance> _Instances;

        // Fast sRGB->linear (matches Unity FastSRGBToLinear polynomial). Authored team colors are sRGB.
        float3 SrgbToLinearFast(float3 c)
        {
            return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878);
        }

        // Two-row temporal lerp with LoopClose wrap. u = UV2.x column (texel center already baked in).
        void SampleVatPose(float u, float phase01, out float3 localPos, out float3 localNrm)
        {
            float rows = _VatRows;
            float ff = phase01 * rows;
            float baseRow = floor(ff);
            float frac = ff - baseRow;
            float rowA = fmod(baseRow, rows);
            float rowB = fmod(rowA + 1.0, rows);
            float vA = (rowA + 0.5) / rows;
            float vB = (rowB + 0.5) / rows;
            float3 pA = SAMPLE_TEXTURE2D_LOD(_PositionVat, sampler_PositionVat, float2(u, vA), 0).xyz;
            float3 pB = SAMPLE_TEXTURE2D_LOD(_PositionVat, sampler_PositionVat, float2(u, vB), 0).xyz;
            float3 nA = SAMPLE_TEXTURE2D_LOD(_NormalVat, sampler_NormalVat, float2(u, vA), 0).xyz;
            float3 nB = SAMPLE_TEXTURE2D_LOD(_NormalVat, sampler_NormalVat, float2(u, vB), 0).xyz;
            localPos = lerp(pA, pB, frac);
            localNrm = lerp(nA, nB, frac); // normalized after the world rotate (below)
        }

        // Shared VAT vertex function: builds world pos+normal+albedo for one vertex of one instance.
        void VatWorld(uint instanceID, float2 uv2, out float3 positionWS, out float3 normalWS, out float3 albedo)
        {
            VatInstance inst = _Instances[instanceID];

            float3 lp, ln;
            SampleVatPose(uv2.x, inst.phase01, lp, ln);
            lp *= inst.scale;

            // Rotate about Y by yaw (matches Unity Quaternion.Euler(0, yawDeg, 0)).
            float s = sin(inst.yaw);
            float c = cos(inst.yaw);
            float3 rp = float3(c * lp.x + s * lp.z, lp.y, -s * lp.x + c * lp.z);
            float3 rn = float3(c * ln.x + s * ln.z, ln.y, -s * ln.x + c * ln.z);

            positionWS = inst.pos + rp;
            normalWS = normalize(rn); // normalize AFTER temporal lerp + rotate

            uint pc = inst.packedColor;
            float3 srgb = float3(pc & 0xffu, (pc >> 8) & 0xffu, (pc >> 16) & 0xffu) * (1.0 / 255.0);
            albedo = SrgbToLinearFast(srgb);
        }
        ENDHLSL

        // ---------------------------------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ForwardVertex
            #pragma fragment ForwardFragment

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float2 uv2 : TEXCOORD1;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 albedo : TEXCOORD2;
                float fogCoord : TEXCOORD3;
                float3 vertexLighting : TEXCOORD4; // per-vertex additional lighting(_ADDITIONAL_LIGHTS_VERTEX 품질에서만 채움)
            };

            Varyings ForwardVertex(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                float3 posWS, nWS, albedo;
                VatWorld(IN.instanceID, IN.uv2, posWS, nWS, albedo);
                OUT.positionWS = posWS;
                OUT.normalWS = nWS;
                OUT.albedo = albedo;
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.fogCoord = ComputeFogFactor(OUT.positionCS.z);
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                    OUT.vertexLighting = VertexLighting(posWS, nWS); // Per-Vertex 추가 광원을 정점에서 적산
                #endif
                return OUT;
            }

            half4 ForwardFragment(Varyings IN) : SV_Target
            {
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = IN.albedo;
                surfaceData.metallic = 0.0;
                surfaceData.specular = 0.0;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;

                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.normalWS = normalize(IN.normalWS);
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord = IN.fogCoord;
                inputData.bakedGI = SampleSH(inputData.normalWS); // global ambient SH (no per-renderer probes in Stage 1)
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                    inputData.vertexLighting = IN.vertexLighting; // Per-Vertex 추가 광원 기여(off면 (InputData)0의 기본 0)
                #endif

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, IN.fogCoord);
                return color;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float2 uv2 : TEXCOORD1;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float4 GetShadowClip(float3 positionWS, float3 normalWS)
            {
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            Varyings ShadowVertex(Attributes IN)
            {
                Varyings OUT;
                float3 posWS, nWS, albedo;
                VatWorld(IN.instanceID, IN.uv2, posWS, nWS, albedo);
                OUT.positionCS = GetShadowClip(posWS, nWS); // animated world normal drives depth bias
                return OUT;
            }

            half4 ShadowFragment(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment

            struct Attributes
            {
                float2 uv2 : TEXCOORD1;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVertex(Attributes IN)
            {
                Varyings OUT;
                float3 posWS, nWS, albedo;
                VatWorld(IN.instanceID, IN.uv2, posWS, nWS, albedo);
                OUT.positionCS = TransformWorldToHClip(posWS);
                return OUT;
            }

            half4 DepthFragment(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float2 uv2 : TEXCOORD1;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            Varyings DepthNormalsVertex(Attributes IN)
            {
                Varyings OUT;
                float3 posWS, nWS, albedo;
                VatWorld(IN.instanceID, IN.uv2, posWS, nWS, albedo);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.normalWS = nWS;
                return OUT;
            }

            half4 DepthNormalsFragment(Varyings IN) : SV_Target
            {
                float3 normalWS = normalize(IN.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                    half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
                    return half4(packedNormalWS, 0.0);
                #else
                    return half4(NormalizeNormalPerPixel(normalWS), 0.0);
                #endif
            }
            ENDHLSL
        }
    }

    FallBack Off
}

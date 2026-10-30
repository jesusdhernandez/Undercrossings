// Alpha-clipped sprite lighting uses one entity anchor instead of the tilted card surface.
Shader "Worldsides/Pixel Sprite Lit"
{
    // Expose texture/tint and lighting controls; realtime shadow reception is optional.
    Properties
    {
        [MainTexture] _BaseMap ("Sprite Texture", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _AmbientStrength ("Ambient Light", Range(0, 1)) = 0
        _DirectLightStrength ("Direct Light Strength", Range(0, 4)) = 1
        _LightingHeight ("Lighting Sample Height", Range(0, 2)) = 0.4
        [Toggle] _ReceiveRealtimeShadows ("Receive Realtime Shadows", Float) = 0
        [HideInInspector] _Cull ("Cull", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForwardOnly" }

            // Draw both sides of the card and write depth only for opaque sprite pixels.
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // Support native URP main/additional lights, shadows, cookies, and Forward+ clusters.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _LIGHT_COOKIES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "OverflowPointLights.hlsl"

            // The vertex stage passes projected geometry, sheet UVs, and a constant light anchor.
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                nointerpolation float3 lightingPositionWS : TEXCOORD2;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            // Keep this per-material layout identical in both passes for SRP batching.
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _AmbientStrength;
                half _DirectLightStrength;
                half _LightingHeight;
                half _ReceiveRealtimeShadows;
            CBUFFER_END

            // Render the authored quad normally but light it as a single billboard entity.
            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions =
                    GetVertexPositionInputs(input.positionOS.xyz);

                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                // Every pixel in a billboard represents the same entity. Sample
                // lighting once at a world-space anchor instead of across the
                // physically tilted card, which otherwise creates diagonal bands
                // and makes front/back placement affect different body pixels.
                output.lightingPositionWS =
                    TransformObjectToWorld(float3(0, 0, 0)) +
                    float3(0, _LightingHeight, 0);
                return output;
            }

            // Ignore the quad normal; nearby lights should illuminate sprites from every direction.
            half3 LightContribution(Light light)
            {
                // Blend shadow attenuation out entirely when Receive Realtime Shadows is disabled.
                half shadowAttenuation = lerp(
                    1,
                    light.shadowAttenuation,
                    saturate(_ReceiveRealtimeShadows)
                );

                // A billboard has no meaningful 3D-facing normal. Applying
                // Lambert lighting here made a light just below the virtual
                // sample height contribute zero, even when it was visibly close.
                // Treat sprites as omnidirectional receivers instead.
                return
                    light.color *
                    light.distanceAttenuation *
                    shadowAttenuation *
                    _DirectLightStrength;
            }

            // Sample the selected animation cell, reject transparent pixels, then apply total light.
            half4 Frag(Varyings input) : SV_Target
            {
                half4 albedo =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) *
                    _BaseColor;


                // Alpha clipping keeps transparent parts from writing depth or creating solid rectangles.
                clip(albedo.a - _Cutoff);


                // Explicit ambient is the only minimum brightness; no hidden sky tint is added.
                half3 lighting = half3(
                    _AmbientStrength,
                    _AmbientStrength,
                    _AmbientStrength
                );

                float4 shadowCoord =
                    TransformWorldToShadowCoord(input.lightingPositionWS);
                lighting += LightContribution(GetMainLight(shadowCoord));


                // Gather additional lights at the entity anchor, using the actual pixel's cluster index.
                #if defined(_ADDITIONAL_LIGHTS) || USE_CLUSTER_LIGHT_LOOP
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.lightingPositionWS;

                    // Forward+ expects the rasterized fragment position here.
                    // The lighting anchor is world-space only; converting its
                    // clip position as if it were a pixel coordinate selects an
                    // unrelated light cluster and can leave the sprite black.
                    inputData.normalizedScreenSpaceUV =
                        GetNormalizedScreenSpaceUV(input.positionCS);

                    uint pixelLightCount = GetAdditionalLightsCount();

                    // Forward+ stores additional directional lights before point/spot lights.
                    #if USE_CLUSTER_LIGHT_LOOP
                        [loop]
                        for (uint lightIndex = 0;
                             lightIndex < min(
                                 URP_FP_DIRECTIONAL_LIGHTS_COUNT,
                                 MAX_VISIBLE_LIGHTS
                             );
                             ++lightIndex)
                        {
                            Light light = GetAdditionalLight(
                                lightIndex,
                                input.lightingPositionWS,
                                half4(1, 1, 1, 1)
                            );
                            lighting += LightContribution(light);
                        }
                    #endif

                    // URP chooses the relevant point/spot lights for the current renderer/cluster.
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light light = GetAdditionalLight(
                            lightIndex,
                            input.lightingPositionWS,
                            half4(1, 1, 1, 1)
                        );
                        lighting += LightContribution(light);
                    LIGHT_LOOP_END
                #endif


                // Keep later placed lights visible even after the native light list fills up.
                lighting += WorldsidesOverflowLighting(input.lightingPositionWS, false) * _DirectLightStrength;
                return half4(albedo.rgb * lighting, 1);
            }
            ENDHLSL
        }

        // Optional shadow-map pass: render only sprite opacity, not visible color.
        // The current capsule proxy normally uses its own material; this pass serves cards using this shader.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            // Shadow rendering needs position, normal (for bias), and UV (for alpha clipping).
            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Cutoff;
                half _AmbientStrength;
                half _DirectLightStrength;
                half _LightingHeight;
                half _ReceiveRealtimeShadows;
            CBUFFER_END

            float3 _LightDirection;
            float3 _LightPosition;

            // Position the geometry in the light's view and offset it slightly to limit shadow acne.
            ShadowVaryings ShadowVert(ShadowAttributes input)
            {
                ShadowVaryings output;

                float3 positionWS =
                    TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS =
                    TransformObjectToWorldNormal(input.normalOS);


                // Point/spot lights use position-to-light direction; directional lights supply a fixed vector.
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirectionWS =
                        normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS)
                );


                // Clamp to the near plane with the depth convention of the current graphics backend.
                #if UNITY_REVERSED_Z
                    positionCS.z = min(
                        positionCS.z,
                        UNITY_NEAR_CLIP_VALUE
                    );
                #else
                    positionCS.z = max(
                        positionCS.z,
                        UNITY_NEAR_CLIP_VALUE
                    );
                #endif

                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            // Transparent sprite pixels must not write into the light's shadow depth map.
            half4 ShadowFrag(ShadowVaryings input) : SV_Target
            {
                half alpha =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a *
                    _BaseColor.a;

                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}

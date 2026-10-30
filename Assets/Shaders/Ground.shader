// Generates stable pixel-sized dirt from world XZ coordinates and lights it with URP lights.
Shader "Worldsides/Ground"
{
    // Material controls: dirt palette, world pixel size, repeatable seed, and light strengths.
    Properties
    {
        _DarkDirt   ("Dark Dirt", Color) = (0.16, 0.09, 0.045, 1)
        _MidDirt    ("Mid Dirt", Color) = (0.28, 0.16, 0.075, 1)
        _LightDirt  ("Light Dirt", Color) = (0.42, 0.27, 0.13, 1)
        _DryDirt    ("Dry Dirt", Color) = (0.55, 0.38, 0.19, 1)
        _PixelSize  ("World Units Per Pixel", Float) = 0.25
        _Seed       ("Seed", Float) = 17
        _AmbientStrength ("Minimum Ambient Light", Range(0, 1)) = 0
        _DirectLightStrength ("Direct Light Strength", Range(0, 4)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "UniversalForward"

            // This shader calculates its own lighting. UniversalForwardOnly
            // keeps it in URP's forward lighting path across renderer modes.
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // Compile variants for supported shadow, extra-light, Forward+, and cookie features.
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

            // Vertex input uses object-space mesh data; varyings carry world data to each pixel.
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;

                // Begin with explicit ambient light; zero ambient permits completely black unlit ground.
                float3 normalWS   : TEXCOORD1;
            };

            // Group per-material values for Unity's SRP batching path.
            CBUFFER_START(UnityPerMaterial)
                half4 _DarkDirt;
                half4 _MidDirt;
                half4 _LightDirt;
                half4 _DryDirt;
                float _PixelSize;
                float _Seed;
                float _AmbientStrength;
                float _DirectLightStrength;
            CBUFFER_END

            // Deterministically turn a cell coordinate into a 0..1 pseudo-random value.
            float Hash(float2 p)
            {
                p = frac(p * float2(0.1031, 0.1030));
                p += dot(p, p.yx + 33.33);
                return frac((p.x + p.y) * p.x);
            }

            // Lambert diffuse brightens upward-facing ground, attenuated by range and shadows.
            half3 LightContribution(Light light, half3 normalWS)
            {
                half diffuse = saturate(dot(normalWS, light.direction));

                return
                    light.color *
                    diffuse *
                    light.distanceAttenuation *
                    light.shadowAttenuation *
                    _DirectLightStrength;
            }

            // Transform the plane once per vertex; pixel noise later uses these world coordinates.
            Varyings Vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs positions =
                    GetVertexPositionInputs(input.positionOS.xyz);

                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.normalWS =
                    TransformObjectToWorldNormal(input.normalOS);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // Each cell is one visible ground "pixel".
                float2 cell = floor(input.positionWS.xz / max(_PixelSize, 0.001));


                // Blend fine grains with larger patches; each scale uses a different seed offset.
                float fineNoise = Hash(cell + _Seed);
                float mediumNoise = Hash(floor(cell / 4.0) + _Seed * 2.13);
                float broadNoise = Hash(floor(cell / 16.0) + _Seed * 4.71);

                float noise =
                    fineNoise * 0.50 +
                    mediumNoise * 0.30 +
                    broadNoise * 0.20;

                half4 dirt;


                // Quantize noise into four palette colors rather than a continuous gradient.
                if (noise < 0.28)
                    dirt = _DarkDirt;
                else if (noise < 0.54)
                    dirt = _MidDirt;
                else if (noise < 0.78)
                    dirt = _LightDirt;
                else
                    dirt = _DryDirt;

                float3 normalWS = normalize(input.normalWS);
                float4 shadowCoord =
                    TransformWorldToShadowCoord(input.positionWS);

                half3 lighting = half3(
                    _AmbientStrength,
                    _AmbientStrength,
                    _AmbientStrength
                );

                // URP treats the brightest directional light as the main light.
                Light mainLight = GetMainLight(shadowCoord);
                lighting += LightContribution(mainLight, normalWS);

                // Point and spot lights are normally additional lights. This
                // loop includes their color, range/spot falloff, and shadows.
                #if defined(_ADDITIONAL_LIGHTS) || USE_CLUSTER_LIGHT_LOOP
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalizedScreenSpaceUV =
                        GetNormalizedScreenSpaceUV(input.positionCS);

                    uint pixelLightCount = GetAdditionalLightsCount();

                    // Clustered/Forward+ can also place extra directional lights
                    // ahead of its punctual-light list.
                    #if USE_CLUSTER_LIGHT_LOOP
                        [loop]
                        for (uint lightIndex = 0;
                             lightIndex < min(
                                 URP_FP_DIRECTIONAL_LIGHTS_COUNT,
                                 MAX_VISIBLE_LIGHTS
                             );
                             ++lightIndex)
                        {
                            Light additionalLight = GetAdditionalLight(
                                lightIndex,
                                input.positionWS,
                                half4(1, 1, 1, 1)
                            );

                            lighting += LightContribution(
                                additionalLight,
                                normalWS
                            );
                        }
                    #endif

                    // URP's loop macro selects point/spot lights in Forward or Forward+ mode.
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light additionalLight = GetAdditionalLight(
                            lightIndex,
                            input.positionWS,
                            half4(1, 1, 1, 1)
                        );

                        lighting += LightContribution(
                            additionalLight,
                            normalWS
                        );
                    LIGHT_LOOP_END
                #endif


                // Extra lights beyond URP's native limit use the shared unshadowed fallback list.
                lighting += WorldsidesOverflowLighting(input.positionWS, true) * _DirectLightStrength;
                dirt.rgb *= lighting;
                return dirt;
            }
            ENDHLSL
        }

        // Reuse URP geometry shadow casting if this ground renderer is configured to cast.
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }
}

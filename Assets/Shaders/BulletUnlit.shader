// A self-lit bullet keeps its orange texture/fallback visible independently of scene lighting.
Shader "Worldsides/Bullet Unlit"
{
    // Optional artwork replaces, rather than multiplies, the fallback color to avoid unwanted tint.
    Properties
    {
        [MainTexture] _BaseMap ("Bullet Texture (optional)", 2D) = "white" {}
        [MainColor] _BaseColor ("Bullet Glow Color", Color) = (1, 0.6060061, 0, 1)
        [Toggle] _UseTexture ("Use Bullet Texture", Float) = 0
        _Fade ("Range Fade", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }

            // Display either side of the quad; alpha clipping keeps transparent texels invisible.
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Texture sampler and per-material fallback settings are shared by both shader stages.
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _UseTexture;
                half _Fade;
            CBUFFER_END

            // Carry mesh UVs from object-space vertices to the rasterized pixel stage.
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // Use Unity's object-to-clip transform so the quad follows its bullet GameObject.
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            // Select artwork or fallback and discard pixels below the alpha cutoff.
            half4 Frag(Varyings input) : SV_Target
            {
                half4 color = lerp(
                    _BaseColor,
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv),
                    saturate(_UseTexture)
                );

                clip(color.a - 0.5);
                // Clip the original pixel silhouette before fading so its edges do not disappear early.
                color.a *= _Fade;
                return color;
            }
            ENDHLSL
        }
    }
}
